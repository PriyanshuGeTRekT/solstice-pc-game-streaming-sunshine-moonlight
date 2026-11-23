using System.Collections.Specialized;
using System.Text.Json.Nodes;
using static Solstice.WebHost;

namespace Solstice;

/// <summary>Everything the PC app's UI can ask for.</summary>
static class HostApi
{
    public static async Task<JsonNode> Handle(string method, string path, JsonObject body, NameValueCollection query)
    {
        switch (method + " " + path)
        {
            case "GET /api/state":
                return await State(query["refresh"] == "1");

            // ---- setup ----
            case "POST /api/setup/start":
                Setup.Start();
                return Ok();
            case "GET /api/setup/status":
                return Setup.Status();

            case "POST /api/login":
            {
                var user = body.Str("user", "").Trim();
                var pass = body.Str("pass", "");
                if (user.Length == 0 || pass.Length == 0) return Err("Enter both the username and the password.");
                var st = await Sunshine.Status((user, pass));
                if (st == ApiStatus.Down) return Err("Sunshine is not answering right now.");
                if (st != ApiStatus.Ok) return Err("Sunshine did not accept that username and password.");
                CredentialStore.Save(user, pass);
                if (!Settings.Load().Bool("setupDone", false)) Setup.Resume();
                return Ok();
            }
            case "POST /api/login/reset":
            {
                var problem = await Setup.ResetLogin();
                if (problem != null) return Err(problem);
                if (!Settings.Load().Bool("setupDone", false)) Setup.Resume();
                return Ok();
            }
            case "GET /api/login/reveal":
            {
                var c = CredentialStore.Load();
                return c == null ? Err("No login is saved.") : Ok(o => { o["user"] = c.Value.User; o["pass"] = c.Value.Pass; o["url"] = Sunshine.WebUiUrl; });
            }

            // ---- service ----
            case "POST /api/service/restart":
            {
                if (await Sunshine.Status() == ApiStatus.Ok)
                {
                    await Sunshine.Restart();
                }
                else
                {
                    var code = await Proc.PowerShellElevated($"Set-Service {Sunshine.ServiceName} -StartupType Automatic; Restart-Service {Sunshine.ServiceName} -Force", 120000);
                    if (code == null) return Err("Administrator approval was declined.");
                }
                await Task.Delay(1500);
                return await Sunshine.WaitUntilUp(45) ? Ok() : Err("Sunshine did not come back. Restart Windows if this keeps happening.");
            }
            case "POST /api/session/end":
                await Sunshine.CloseRunningApp();
                return Ok();

            // ---- devices ----
            case "GET /api/devices":
            {
                var clients = await Sunshine.GetClients();
                var pending = await Sunshine.PendingPairings();
                return Ok(o => { o["clients"] = clients.DeepClone(); o["pending"] = pending.DeepClone(); o["link"] = DeviceLink.Status(); });
            }
            case "POST /api/devices/pair":
            {
                var pin = body.Str("pin", "").Trim();
                var name = body.Str("name", "").Trim();
                if (pin.Length != 4 || !pin.All(char.IsAsciiDigit)) return Err("The PIN is the 4 digits Moonlight is showing on the device.");
                if (name.Length == 0) name = "My device";
                var pending = await Sunshine.PendingPairings();
                if (pending.Count == 0)
                    return Err("No device is asking to pair. On the device, open Moonlight and select this PC first; it then shows a PIN.");
                var id = body.Str("pairingId", "");
                if (id.Length == 0) id = pending.OfType<JsonObject>().Last()["id"]?.ToString() ?? "";
                return await Sunshine.SubmitPin(id, pin, name)
                    ? Ok()
                    : Err("That PIN did not match. Check the number on the device and try again.");
            }
            case "POST /api/devices/unpair":
                return await Sunshine.Unpair(body.Str("uuid", "")) ? Ok() : Err("That device could not be removed.");

            case "POST /api/link/start":
            {
                var link = await DeviceLink.Start();
                return Ok(o => o["link"] = link);
            }
            case "POST /api/link/stop":
                DeviceLink.Stop();
                return Ok();
            case "POST /api/link/decide":
                DeviceLink.Decide(body.Bool("approve", false));
                return Ok();
            case "GET /api/link":
                return Ok(o => o["link"] = DeviceLink.Status());

            // ---- library ----
            case "GET /api/library":
            {
                var snap = await Library.Snapshot();
                return Ok(o => { o["items"] = snap["items"]!.DeepClone(); o["other"] = snap["other"]!.DeepClone(); });
            }
            case "POST /api/library/sync":
            {
                var ids = (body["ids"] as JsonArray ?? new JsonArray()).Select(i => i!.ToString()).ToHashSet();
                var r = await Library.Sync(ids);
                return Ok(o => { o["added"] = r["added"]!.DeepClone(); o["removed"] = r["removed"]!.DeepClone(); });
            }

            // ---- tuning ----
            case "GET /api/tuning":
                return Ok(o => o["tuning"] = Tuning.Current());
            case "POST /api/tuning":
            {
                var result = await Tuning.Apply(body);
                return Ok(o => { o["result"] = result; o["tuning"] = Tuning.Current(); });
            }

            // ---- fixes that change Windows settings (each asks for administrator approval) ----
            case "POST /api/fix/private-network":
            {
                var host = await Probe.Host();
                var index = host["nic"]?["index"]?.ToString();
                if (index == null) return Err("No active network adapter was found.");
                var code = await Proc.PowerShellElevated($"Set-NetConnectionProfile -InterfaceIndex {int.Parse(index)} -NetworkCategory Private", 60000);
                if (code == null) return Err("Administrator approval was declined.");
                Probe.Invalidate();
                return Ok();
            }
            case "POST /api/fix/wake":
            {
                var host = await Probe.Host();
                var alias = host["nic"]?["alias"]?.ToString();
                var desc = host["nic"]?["description"]?.ToString();
                if (alias == null || desc == null) return Err("No active network adapter was found.");
                var code = await Proc.PowerShellElevated(
                    $"Set-NetAdapterAdvancedProperty -Name {Proc.Q(alias)} -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1; powercfg /deviceenablewake {Proc.Q(desc)}", 60000);
                if (code == null) return Err("Administrator approval was declined.");
                Probe.Invalidate();
                return Ok();
            }

            // ---- misc ----
            case "GET /api/logs":
                return Ok(o => o["lines"] = new JsonArray(Sunshine.LogTail(300).Select(l => (JsonNode)l).ToArray()));
            case "POST /api/open":
                switch (body.Str("what", ""))
                {
                    case "webui": Proc.OpenExternal(Sunshine.WebUiUrl); break;
                    case "logs": Proc.OpenExternal(Sunshine.ConfigDir); break;
                    case "client-file":
                        if (DeviceLink.ClientExe() is { } exe)
                            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{exe}\"");
                        break;
                    case "source-sunshine": Proc.OpenExternal("https://github.com/LizardByte/Sunshine"); break;
                    case "source-moonlight": Proc.OpenExternal("https://github.com/moonlight-stream/moonlight-qt"); break;
                }
                return Ok();
        }
        return Err("Unknown request.");
    }

    public static string? File(string key) => key.StartsWith("cover/") ? Library.CoverFor(key["cover/".Length..]) : null;

    static async Task<JsonObject> State(bool refresh)
    {
        var host = await Probe.Host(refresh);
        var installed = Sunshine.Installed;
        var service = installed ? Sunshine.ServiceState() : "Missing";
        var api = installed && service == "Running" ? await Sunshine.Status() : ApiStatus.Down;
        var activity = installed
            ? Probe.Activity()
            : new JsonObject { ["streaming"] = false, ["encoders"] = new JsonArray(), ["events"] = new JsonArray(), ["gamepadMissing"] = false };
        var setupDone = Settings.Load().Bool("setupDone", false);
        var vigem = host["vigem"]?.GetValue<bool>() == true;
        var firewall = host["firewall"]?.GetValue<bool>() == true;
        var encoders = activity["encoders"]!.AsArray().Select(e => e!.ToString()).ToArray();
        var ready = setupDone && installed && service == "Running" && api == ApiStatus.Ok && vigem;

        var checks = new JsonArray();
        void Check(string id, string title, string state, string detail, string? action = null, string? actionLabel = null) =>
            checks.Add(new JsonObject { ["id"] = id, ["title"] = title, ["state"] = state, ["detail"] = detail, ["action"] = action, ["actionLabel"] = action == null ? null : actionLabel });

        Check("engine", "Streaming engine",
            installed && service == "Running" ? "ok" : "bad",
            !installed ? "Sunshine is not installed."
                : service == "Running" ? $"Sunshine {Sunshine.Version()} is running and starts with Windows."
                : $"The Sunshine service is {service.ToLowerInvariant()}.",
            !installed ? "setup" : service == "Running" ? null : "restart", !installed ? "Run setup" : "Start it");

        if (installed && service == "Running")
        {
            Check("login", "Private login",
                api == ApiStatus.Ok ? "ok" : api == ApiStatus.Down ? "warn" : "bad",
                api switch
                {
                    ApiStatus.Ok => "Solstice holds the Sunshine login, encrypted for your Windows account.",
                    ApiStatus.NoCredentials => "Sunshine has no login yet.",
                    ApiStatus.NeedLogin => "Sunshine has a login that Solstice does not know.",
                    _ => "Sunshine is still starting.",
                },
                api switch { ApiStatus.NoCredentials => "setup", ApiStatus.NeedLogin => "login", _ => null },
                api == ApiStatus.NoCredentials ? "Run setup" : "Sign in");

            Check("encoder", "Video encoder",
                encoders.Length > 0 ? "ok" : "warn",
                encoders.Length > 0 ? $"{string.Join(", ", encoders)} hardware encoding on {host["gpu"]}." : "Sunshine is still testing the graphics card.");
        }

        Check("controller", "Controller driver", vigem ? "ok" : "bad",
            vigem ? "ViGEmBus is installed: the handheld's buttons and sticks arrive as an Xbox controller."
                  : "ViGEmBus is missing, so controller input will not reach games.",
            vigem ? null : "setup", "Run setup");

        if (host["nic"] != null)
        {
            var wired = host["wired"]!.GetValue<bool>();
            Check("network", "Network", wired ? "ok" : "warn",
                wired ? $"Wired at {host["nic"]!["speed"]}. This PC's address is {host["ip"]}."
                      : $"This PC is on Wi-Fi ({host["nic"]!["speed"]}). A cable to the router is the biggest single improvement to streaming.");

            if (installed)
                Check("firewall", "Firewall", firewall ? "ok" : "bad",
                    firewall ? "Windows Firewall lets devices reach Sunshine." : "Windows Firewall is blocking Sunshine.",
                    firewall ? null : "setup", "Run setup");

            if (host["wol"] is JsonValue wolv && wolv.TryGetValue<bool>(out var wol))
                Check("wake", "Wake from sleep", wol ? "ok" : "warn",
                    wol ? "Wake-on-LAN is on: the handheld can wake this PC when it is asleep."
                        : "Wake-on-LAN is off, so a sleeping PC cannot be woken from the handheld.",
                    wol ? null : "wake", "Turn on");

            if (host["category"]?.ToString() == "Public")
                Check("discovery", "Network type", "warn",
                    "Windows treats this network as Public. Streaming works, and Solstice Play finds this PC by scanning; plain Moonlight may need the address typed in.",
                    "private-network", "Mark as Private");
        }
        else
        {
            Check("network", "Network", "bad", "This PC is not connected to a network.");
        }

        return new JsonObject
        {
            ["version"] = AppInfo.Version,
            ["host"] = host,
            ["sunshine"] = new JsonObject
            {
                ["installed"] = installed, ["version"] = Sunshine.Version(), ["service"] = service,
                ["api"] = api.ToString(), ["webUi"] = Sunshine.WebUiUrl,
            },
            ["activity"] = activity,
            ["tuning"] = Tuning.Current(),
            ["setupDone"] = setupDone,
            ["ready"] = ready,
            ["setup"] = Setup.Status(),
            ["checks"] = checks,
            ["link"] = DeviceLink.Status(),
        };
    }
}
