using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solstice;

/// <summary>Reads hardware, network and driver facts about this PC. Slow parts are cached.</summary>
static class Probe
{
    static JsonObject? _host;
    static DateTime _hostAt;
    static readonly SemaphoreSlim Gate = new(1, 1);

    const string Script = """
        $gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object {
          @{ name = $_.Name; w = $_.CurrentHorizontalResolution; h = $_.CurrentVerticalResolution; hz = $_.CurrentRefreshRate } })
        $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
        $ram = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB)
        $route = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1
        $nic = $null; $ip = $null; $cat = $null; $wol = $null
        if ($route) {
          $nic = Get-NetAdapter -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue
          $ip = (Get-NetIPAddress -InterfaceIndex $route.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1).IPAddress
          $cat = "$((Get-NetConnectionProfile -InterfaceIndex $route.InterfaceIndex -ErrorAction SilentlyContinue | Select-Object -First 1).NetworkCategory)"
        }
        if ($nic) {
          $w = Get-NetAdapterAdvancedProperty -Name $nic.Name -RegistryKeyword '*WakeOnMagicPacket' -ErrorAction SilentlyContinue
          if ($w) { $wol = ("$($w.RegistryValue)" -eq '1') }
        }
        $vigem = [bool](Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'Virtual Gamepad Emulation Bus' -and $_.Status -eq 'OK' })
        $fw = [bool](Get-NetFirewallRule -DisplayName 'Sunshine' -ErrorAction SilentlyContinue | Where-Object { "$($_.Enabled)" -eq 'True' -and "$($_.Direction)" -eq 'Inbound' -and "$($_.Action)" -eq 'Allow' })
        $kitfw = [bool](Get-NetFirewallRule -DisplayName 'Solstice device setup' -ErrorAction SilentlyContinue | Where-Object { "$($_.Enabled)" -eq 'True' })
        $winget = [bool](Get-Command winget -ErrorAction SilentlyContinue)
        @{
          name = $env:COMPUTERNAME; cpu = "$cpu".Trim(); ramGb = $ram; gpus = $gpus
          nic = if ($nic) { @{ index = $nic.ifIndex; alias = $nic.Name; description = $nic.InterfaceDescription; speed = "$($nic.LinkSpeed)"; media = "$($nic.PhysicalMediaType)" } } else { $null }
          ip = $ip; category = $cat; wol = $wol; vigem = $vigem; firewall = $fw; kitFirewall = $kitfw; winget = $winget
        } | ConvertTo-Json -Depth 5 -Compress
        """;

    public static async Task<JsonObject> Host(bool refresh = false)
    {
        await Gate.WaitAsync();
        try
        {
            if (!refresh && _host != null && DateTime.UtcNow - _hostAt < TimeSpan.FromMinutes(10))
                return (JsonObject)_host.DeepClone();

            var res = await Proc.PowerShell(Script, 45000);
            JsonObject o;
            try { o = JsonNode.Parse(res.Out.Trim())!.AsObject(); }
            catch { o = new JsonObject { ["name"] = Environment.MachineName, ["error"] = "Could not read system details." }; }

            // Pick the GPU that is actually driving a display, and name the hardware encoder family.
            var gpus = o["gpus"] as JsonArray ?? new JsonArray();
            JsonObject? active = gpus.OfType<JsonObject>().FirstOrDefault(g => g["w"] is JsonValue v && v.TryGetValue<int>(out var w) && w > 0)
                                 ?? gpus.OfType<JsonObject>().FirstOrDefault();
            var gpuName = active?["name"]?.ToString() ?? "Unknown GPU";
            var all = string.Join(" ", gpus.OfType<JsonObject>().Select(g => g["name"]?.ToString()));
            o["gpu"] = gpuName;
            o["vendor"] = all.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "nvidia"
                : all.Contains("AMD", StringComparison.OrdinalIgnoreCase) || all.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "amd"
                : all.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "intel" : "other";
            o["display"] = active == null ? null : new JsonObject
            {
                ["w"] = active["w"]?.DeepClone(),
                ["h"] = active["h"]?.DeepClone(),
                ["hz"] = active["hz"]?.DeepClone(),
            };
            var media = o["nic"]?["media"]?.ToString() ?? "";
            o["wired"] = media.Contains("802.3");
            _host = o;
            _hostAt = DateTime.UtcNow;
            return (JsonObject)o.DeepClone();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Invalidate() => _hostAt = DateTime.MinValue;

    static readonly Regex Stamp = new(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d+\]: (\w+): (.*)$", RegexOptions.Compiled);

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint XInputGetState(uint index, out XInputState state);

    /// <summary>
    /// How many controllers Windows reports in its four XInput slots right now: physical pads
    /// plus, while a device is streaming, the virtual pad Sunshine creates for it.
    /// </summary>
    public static int ControllersConnected()
    {
        var n = 0;
        try
        {
            for (uint i = 0; i < 4; i++)
                if (XInputGetState(i, out _) == 0) n++;
        }
        catch { /* XInput unavailable */ }
        return n;
    }

    /// <summary>Maps each published app's command (tracked or detached) to its name, from apps.json.</summary>
    static Dictionary<string, string> AppNamesByCommand()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var apps = JsonNode.Parse(File.ReadAllText(Path.Combine(Sunshine.ConfigDir, "apps.json")))?["apps"] as JsonArray;
            foreach (var a in apps?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var name = a["name"]?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                var cmd = a["cmd"]?.ToString();
                if (!string.IsNullOrEmpty(cmd)) map[cmd] = name;
                foreach (var d in (a["detached"] as JsonArray)?.Select(x => x?.ToString()) ?? Enumerable.Empty<string?>())
                    if (!string.IsNullOrEmpty(d)) map[d] = name;
            }
        }
        catch { /* names fall back to what the log says */ }
        return map;
    }

    /// <summary>
    /// Works out from Sunshine's log whether a device is streaming right now, which encoders
    /// passed their self-test, and the recent connect/disconnect history.
    /// </summary>
    public static JsonObject Activity()
    {
        var lines = Sunshine.LogTail(4000);
        var events = new JsonArray();
        var encoders = new List<string>();
        var live = false;
        string? since = null, app = null, lastApp = null, spawned = null;
        var gamepadMissing = false;
        string? trayQuit = null;
        var names = AppNamesByCommand();

        foreach (var line in lines)
        {
            var m = Stamp.Match(line);
            if (!m.Success) continue;
            var (time, text) = (m.Groups[1].Value, m.Groups[3].Value);

            if (text.StartsWith("Sunshine version"))
            {
                // Service (re)started: anything before this point is a previous run.
                live = false;
                since = null;
                encoders.Clear();
                gamepadMissing = false;
                trayQuit = null;
            }
            else if (text.StartsWith("Quitting from system tray"))
            {
                // Sunshine's tray icon has a Quit item that stops the whole streaming service.
                trayQuit = time;
                live = false;
            }
            else if (text.StartsWith("Found H.264 encoder")) encoders.Add("H.264");
            else if (text.StartsWith("Found HEVC encoder")) encoders.Add("HEVC");
            else if (text.StartsWith("Found AV1 encoder")) encoders.Add("AV1");
            else if (text.Contains("No gamepad input is available")) gamepadMissing = true;
            else if (text.StartsWith("Spawning"))
            {
                // Detached launches log the command they spawn, followed by "Executing [Desktop]".
                var a = Regex.Match(text, @"^Spawning \[(.+?)\] in ");
                if (a.Success) spawned = a.Groups[1].Value;
            }
            else if (text.StartsWith("Executing"))
            {
                // "Executing [App name]" or, for tracked commands, "Executing: [command] in [dir]".
                var a = Regex.Match(text, @"\[(.+?)\]");
                if (a.Success)
                {
                    var key = spawned ?? a.Groups[1].Value;
                    lastApp = names.TryGetValue(key, out var n) ? n : a.Groups[1].Value;
                }
                spawned = null;
            }
            else if (text.Contains("CLIENT CONNECTED"))
            {
                live = true;
                since = time;
                app = lastApp;
                events.Add(new JsonObject { ["time"] = time, ["type"] = "connected", ["app"] = app });
            }
            else if (live && (text.Contains("CLIENT DISCONNECTED") || text.StartsWith("Session ended")
                              || text.StartsWith("Process terminated")
                              || text.StartsWith("Trying to revert applied display device settings")))
            {
                // A clean exit logs CLIENT DISCONNECTED. A dropped or killed client does not, so the
                // app being terminated or the monitor being switched back also count as the end.
                live = false;
                events.Add(new JsonObject { ["time"] = time, ["type"] = "disconnected" });
            }
        }

        var streaming = live;
        var recent = new JsonArray();
        foreach (var e in events.Reverse().Take(12)) recent.Add(e!.DeepClone());
        return new JsonObject
        {
            ["streaming"] = streaming,
            ["since"] = streaming ? since : null,
            ["app"] = streaming ? app : null,
            ["encoders"] = new JsonArray(encoders.Distinct().Select(e => (JsonNode)e).ToArray()),
            ["gamepadMissing"] = gamepadMissing,
            ["trayQuit"] = trayQuit,
            ["events"] = recent,
        };
    }
}
