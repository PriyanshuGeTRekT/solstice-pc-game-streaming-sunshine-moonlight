using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solstice;

/// <summary>
/// The handheld side: finds the PC, pairs with it, works out the right stream for this screen,
/// wakes the PC when it is asleep and launches Moonlight.
/// </summary>
static class Play
{
    public const int SunshinePort = 47989;
    public const int LinkPort = 47800;

    static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(140) };

    // ---- screen ---------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool EnumDisplaySettingsW(string? device, int mode, ref DEVMODE dm);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref int len);

    /// <summary>Current mode of the built-in screen, always reported landscape.</summary>
    public static (int W, int H, int Hz) Screen()
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(null, -1, ref dm) || dm.dmPelsWidth < 640) return (1920, 1080, 60);
        int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, hz = (int)dm.dmDisplayFrequency;
        if (h > w) (w, h) = (h, w);
        if (hz < 30) hz = 60;
        return (w, h, hz);
    }

    /// <summary>
    /// A bitrate that looks clean without stressing Wi-Fi: 30 Mbps at 1080p60, scaled by pixel
    /// count and (less than linearly) by frame rate. 1080p at 120 fps comes out at 50 Mbps.
    /// </summary>
    public static int AutoBitrateKbps(int w, int h, int fps)
    {
        var kbps = 30000.0 * (w * (double)h / (1920 * 1080)) * Math.Pow(fps / 60.0, 0.75);
        return (int)Math.Clamp(Math.Round(kbps / 1000) * 1000, 8000, 80000);
    }

    public static StreamSettings Stream()
    {
        var s = Settings.Load();
        var (sw, sh, shz) = Screen();
        var (w, h) = s.Str("resolution", "native") switch
        {
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            _ => (sw, sh),
        };
        var fps = s.Int("fps", 0);
        if (fps <= 0) fps = Math.Min(shz, 120);
        var mbps = s.Int("bitrateMbps", 0);
        var kbps = mbps > 0 ? mbps * 1000 : AutoBitrateKbps(w, h, fps);
        return new StreamSettings(w, h, fps, kbps,
            s.Str("codec", "HEVC"), s.Bool("vsync", false), s.Bool("hdr", false), s.Bool("overlay", false), s.Bool("quitAfter", true));
    }

    // ---- finding the PC -------------------------------------------------------------------

    static async Task<bool> PortOpen(string ip, int port, int timeoutMs)
    {
        using var c = new TcpClient();
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await c.ConnectAsync(IPAddress.Parse(ip), port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    public static Task<bool> Online(string ip) => PortOpen(ip, SunshinePort, 600);

    /// <summary>Asks a streaming host for its name over the standard, unauthenticated info endpoint.</summary>
    static async Task<JsonObject?> Identify(string ip)
    {
        if (!await PortOpen(ip, SunshinePort, 450)) return null;
        var name = ip;
        try
        {
            using var cts = new CancellationTokenSource(2500);
            var xml = await Http.GetStringAsync($"http://{ip}:{SunshinePort}/serverinfo?uniqueid=0123456789ABCDEF", cts.Token);
            var m = Regex.Match(xml, @"<hostname>(.*?)</hostname>");
            if (m.Success) name = WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        catch { /* still a candidate; show it by address */ }
        return new JsonObject { ["ip"] = ip, ["name"] = name, ["solstice"] = await LinkOpen(ip) };
    }

    /// <summary>True when the Solstice app on that PC has its Add device panel open.</summary>
    public static async Task<bool> LinkOpen(string ip)
    {
        if (!await PortOpen(ip, LinkPort, 450)) return false;
        try
        {
            using var cts = new CancellationTokenSource(2000);
            var hello = JsonNode.Parse(await Http.GetStringAsync($"http://{ip}:{LinkPort}/hello", cts.Token));
            return hello?["app"]?.ToString() == "solstice";
        }
        catch { return false; }
    }

    /// <summary>Scans this device's own /24 for streaming hosts. Takes about a second.</summary>
    public static async Task<List<JsonObject>> Discover()
    {
        var targets = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var props = ni.GetIPProperties();
            if (props.GatewayAddresses.Count == 0) continue;
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = ua.Address.GetAddressBytes();
                for (var i = 1; i < 255; i++) targets.Add($"{b[0]}.{b[1]}.{b[2]}.{i}");
            }
        }
        var found = await Task.WhenAll(targets.Select(Identify));
        return found.Where(f => f != null).Select(f => f!)
            .OrderByDescending(f => f["solstice"]!.GetValue<bool>()).ThenBy(f => f["name"]!.ToString()).ToList();
    }

    // ---- wake-on-LAN ----------------------------------------------------------------------

    /// <summary>Hardware address of a device on the local network, from Windows' ARP resolution.</summary>
    public static string? MacOf(string ip)
    {
        try
        {
            var mac = new byte[6];
            var len = mac.Length;
            var dest = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
            if (SendARP(dest, 0, mac, ref len) != 0 || len != 6 || mac.All(x => x == 0)) return null;
            return string.Join(":", mac.Select(x => x.ToString("X2")));
        }
        catch { return null; }
    }

    public static void Wake(string mac, string ip)
    {
        var bytes = mac.Split(':').Select(x => Convert.ToByte(x, 16)).ToArray();
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(bytes, 0, packet, 6 + i * 6, 6);

        var parts = ip.Split('.');
        var targets = new[] { IPAddress.Broadcast, IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.255") };
        using var udp = new UdpClient { EnableBroadcast = true };
        for (var round = 0; round < 3; round++)
            foreach (var t in targets)
                foreach (var port in new[] { 9, 7 })
                {
                    try { udp.Send(packet, packet.Length, new IPEndPoint(t, port)); } catch { /* keep trying the others */ }
                }
    }

    // ---- guided setup ---------------------------------------------------------------------

    public static readonly StepList Steps = NewSteps();
    static JsonArray _hosts = new();
    static string _pin = "", _pairMode = "";

    static StepList NewSteps()
    {
        var s = new StepList();
        Reset(s);
        return s;
    }

    static void Reset(StepList s) => s.Reset(
        ("moonlight", "Install Moonlight"),
        ("find", "Find your PC"),
        ("pair", "Pair with your PC"),
        ("tune", "Tune for this screen"),
        ("finish", "Add to the Start menu"));

    public static JsonObject SetupStatus()
    {
        var o = Steps.Snapshot();
        o["hosts"] = _hosts.DeepClone();
        o["pin"] = _pin;
        o["pairMode"] = _pairMode;
        return o;
    }

    public static bool StartSetup(string? chosenHost)
    {
        if (!Steps.TryStart()) return false;
        Reset(Steps);
        _pin = _pairMode = "";
        _ = Task.Run(async () =>
        {
            try { await RunSetup(chosenHost); }
            catch (StopSetup) { /* the failing step is already recorded */ }
            catch (Exception ex) { Steps.Fail(ex.Message); }
            finally { _pin = _pairMode = ""; Steps.Stop(); }
        });
        return true;
    }

    sealed class StopSetup : Exception;

    static async Task RunSetup(string? chosenHost)
    {
        // 1. Moonlight
        Steps.Begin("moonlight");
        if (Moonlight.Exe() != null)
        {
            Steps.End($"Moonlight {Moonlight.Version()} is installed", "skipped");
        }
        else
        {
            Steps.Detail("Downloading Moonlight. Choose Yes if Windows asks for permission…");
            ProcResult r;
            try
            {
                r = await Proc.Run("winget", new[]
                {
                    "install", "--id", Moonlight.WingetId, "--exact", "--source", "winget", "--silent",
                    "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements",
                }, 600000);
            }
            catch (Exception)
            {
                r = new ProcResult(-1, "", "winget missing");
            }
            if (Moonlight.Exe() == null)
            {
                Steps.Fail(r.Err == "winget missing"
                    ? "Windows Package Manager is not available on this device. Install Moonlight from moonlight-stream.org, then tap Set up again."
                    : "Moonlight did not install. Check the internet connection, or install it from moonlight-stream.org, then tap Set up again.", "no-moonlight");
                throw new StopSetup();
            }
            Steps.End($"Moonlight {Moonlight.Version()} installed");
        }

        // 2. Find the PC
        Steps.Begin("find", "Looking for your PC on this network…");
        var saved = Settings.Load();
        string? ip = chosenHost, name = null;
        if (ip != null)
        {
            if (!IPAddress.TryParse(ip, out _))
            {
                try { ip = (await Dns.GetHostAddressesAsync(ip)).First(a => a.AddressFamily == AddressFamily.InterNetwork).ToString(); }
                catch { Steps.Fail($"\"{chosenHost}\" is not an address this device can reach.", "no-host"); throw new StopSetup(); }
            }
            var id = await Identify(ip);
            if (id == null) { Steps.Fail($"Nothing is streaming at {ip}. Check that Solstice shows Ready on your PC.", "no-host"); throw new StopSetup(); }
            name = id["name"]!.ToString();
        }
        else
        {
            var known = saved.Str("hostIp", "");
            if (known.Length > 0 && await Identify(known) is { } again)
            {
                ip = known;
                name = again["name"]!.ToString();
            }
            else
            {
                var hosts = await Discover();
                _hosts = new JsonArray(hosts.Select(h => (JsonNode)h.DeepClone()).ToArray());
                if (hosts.Count == 0)
                {
                    Steps.Fail("No streaming PC was found on this network. Check that the PC is on, that Solstice shows Ready, and that both devices use the same router.", "no-host");
                    throw new StopSetup();
                }
                var withLink = hosts.Where(h => h["solstice"]!.GetValue<bool>()).ToList();
                var pick = hosts.Count == 1 ? hosts[0] : withLink.Count == 1 ? withLink[0] : null;
                if (pick == null)
                {
                    Steps.Fail("More than one streaming PC is on this network. Choose yours.", "choose-host");
                    throw new StopSetup();
                }
                ip = pick["ip"]!.ToString();
                name = pick["name"]!.ToString();
            }
        }
        var mac = MacOf(ip);
        Settings.Update(s => { s["hostIp"] = ip; s["hostName"] = name; if (mac != null) s["hostMac"] = mac; });
        Steps.End($"{name} at {ip}");

        // 3. Pair
        Steps.Begin("pair", "Checking…");
        if (await Moonlight.IsPaired(ip))
        {
            Steps.End($"Already paired with {name}", "skipped");
        }
        else
        {
            var linked = await LinkOpen(ip);
            _pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
            _pairMode = linked ? "approve" : "type";
            Steps.Detail(linked ? $"Approve this device on {name}" : $"Enter the PIN on {name}");

            using var pairing = Moonlight.Start(new[] { "pair", ip, "--pin", _pin });
            Task<string>? ask = null;
            if (linked)
            {
                await Task.Delay(1500); // let the pairing request reach the PC first
                var device = Uri.EscapeDataString(Environment.MachineName);
                ask = Http.GetStringAsync($"http://{ip}:{LinkPort}/pair?pin={_pin}&name={device}");
            }

            // Moonlight's pairing window does not close on its own, so watch for the result:
            // the pairing is saved once a separate "list" call succeeds, and then the window goes.
            var paired = false;
            var until = DateTime.UtcNow.AddSeconds(150);
            while (DateTime.UtcNow < until && !pairing.HasExited)
            {
                await Task.Delay(2000);
                if (ask is { IsCompleted: true })
                {
                    var declined = ask.IsCompletedSuccessfully && ask.Result.Contains("\"declined\"");
                    ask = null;
                    if (declined) break;
                }
                if (await Moonlight.IsPaired(ip)) { paired = true; break; }
            }
            try { if (!pairing.HasExited) pairing.Kill(true); } catch { /* already closed */ }
            _pin = _pairMode = "";
            if (!paired && !await Moonlight.IsPaired(ip))
            {
                Steps.Fail($"Pairing was not approved on {name}. Tap Set up to try again.", "pair-failed");
                throw new StopSetup();
            }
            Steps.End($"Paired with {name}");
        }

        // 4. Tune
        Steps.Begin("tune");
        var st = Stream();
        Moonlight.SeedSettings(st);
        Steps.End($"{st.Width}×{st.Height} at {st.Fps} fps · {st.BitrateKbps / 1000} Mbps · {st.Codec}");

        // 5. Finish
        Steps.Begin("finish");
        var installed = SelfInstall();
        Settings.Update(s => s["setupDone"] = true);
        Steps.End(installed ? "Solstice Play is in the Start menu and on the desktop" : "Ready", installed ? "done" : "skipped");
    }

    /// <summary>Copies this exe to a permanent per-user location and adds shortcuts. No admin rights needed.</summary>
    static bool SelfInstall()
    {
        if (Environment.GetEnvironmentVariable("SOLSTICE_NO_INSTALL") != null) return false;
        try
        {
            var self = Environment.ProcessPath!;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SolsticePlay");
            var target = Path.Combine(dir, "SolsticePlay.exe");
            if (!string.Equals(Path.GetFullPath(self), target, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(dir);
                File.Copy(self, target, true);
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.DesktopDirectory })
            {
                dynamic lnk = shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(folder), "Solstice Play.lnk"));
                lnk.TargetPath = target;
                lnk.WorkingDirectory = dir;
                lnk.Description = "Play your PC games on this device";
                lnk.Save();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- playing --------------------------------------------------------------------------

    static Process? _stream;
    static List<string>? _apps;
    static DateTime _appsAt;

    public static bool Playing
    {
        get
        {
            try { return _stream is { HasExited: false }; }
            catch { return false; }
        }
    }

    public static async Task<List<string>> Apps(bool refresh)
    {
        var ip = Settings.Load().Str("hostIp", "");
        if (ip.Length == 0) return new List<string>();
        if (!refresh && _apps != null && DateTime.UtcNow - _appsAt < TimeSpan.FromSeconds(45)) return _apps;
        var apps = await Moonlight.Apps(ip);
        if (apps.Count > 0)
        {
            _apps = apps;
            _appsAt = DateTime.UtcNow;
            Settings.Update(s => s["apps"] = new JsonArray(apps.Select(a => (JsonNode)a).ToArray()));
            return apps;
        }
        // PC asleep or unreachable: show what it had last time.
        return (Settings.Load()["apps"] as JsonArray)?.Select(a => a!.ToString()).ToList() ?? new List<string> { "Desktop" };
    }

    /// <summary>Wakes the PC if it is asleep and waits for streaming to come up.</summary>
    public static async Task<bool> EnsureAwake(int seconds)
    {
        var s = Settings.Load();
        var ip = s.Str("hostIp", "");
        if (await Online(ip)) return true;
        var mac = s.Str("hostMac", "");
        if (mac.Length == 0) return false;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Wake(mac, ip);
            await Task.Delay(2500);
            if (await Online(ip)) return true;
        }
        return false;
    }

    public static async Task Launch(string app)
    {
        var s = Settings.Load();
        var ip = s.Str("hostIp", "");
        if (ip.Length == 0) throw new AppException("No PC is set up yet.");
        if (Playing) throw new AppException("A game is already streaming.");
        if (!await EnsureAwake(75))
            throw new AppException($"{s.Str("hostName", "Your PC")} is not answering. If it is switched off or asleep, turn it on and try again.");

        var st = Stream();
        Moonlight.SeedSettings(st);
        _stream = Moonlight.Start(Moonlight.StreamArgs(ip, app, st));
    }

    public static async Task QuitOnHost()
    {
        var ip = Settings.Load().Str("hostIp", "");
        if (ip.Length == 0) return;
        await Moonlight.Run(new[] { "quit", ip }, 30000);
    }
}
