using System.Text;
using System.Text.Json.Nodes;

namespace Solstice;

/// <summary>
/// The one-click setup: installs what is missing, starts the service, secures it with a private
/// login, applies tuned settings and publishes the launchers. Everything that needs administrator
/// rights is batched into a single elevated script so there is at most one Windows prompt.
/// </summary>
static class Setup
{
    public const string KitRuleName = "Solstice device setup";

    sealed class Step(string id, string label)
    {
        public string Id = id, Label = label, State = "pending", Detail = "";
    }

    static readonly object Gate = new();
    static List<Step> _steps = NewSteps();
    static bool _running;
    static string? _problem; // null | "need-login" | "failed" | "declined"

    static List<Step> NewSteps() => new()
    {
        new("scan", "Check this PC"),
        new("install", "Install the streaming engine and controller driver"),
        new("service", "Start the streaming service"),
        new("login", "Secure it with a private login"),
        new("tune", "Apply tuned settings"),
        new("library", "Publish your launchers"),
        new("verify", "Final check"),
    };

    public static bool Running { get { lock (Gate) return _running; } }

    public static JsonObject Status()
    {
        lock (Gate)
        {
            var arr = new JsonArray();
            foreach (var s in _steps)
                arr.Add(new JsonObject { ["id"] = s.Id, ["label"] = s.Label, ["state"] = s.State, ["detail"] = s.Detail });
            return new JsonObject
            {
                ["running"] = _running,
                ["problem"] = _problem,
                ["done"] = !_running && _problem == null && _steps.All(s => s.State is "done" or "skipped"),
                ["steps"] = arr,
            };
        }
    }

    public static bool Start()
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _problem = null;
            _steps = NewSteps();
        }
        _ = Task.Run(async () =>
        {
            try { await RunAll(); }
            catch (Exception ex) { Fail(ex.Message); }
            finally { lock (Gate) _running = false; }
        });
        return true;
    }

    static Step? _current;

    static void Begin(string id, string detail = "")
    {
        lock (Gate)
        {
            _current = _steps.First(s => s.Id == id);
            _current.State = "running";
            _current.Detail = detail;
        }
    }

    static void Detail(string detail) { lock (Gate) if (_current != null) _current.Detail = detail; }

    static void End(string detail, string state = "done")
    {
        lock (Gate)
        {
            if (_current == null) return;
            _current.State = state;
            _current.Detail = detail;
        }
    }

    static void Fail(string detail, string problem = "failed")
    {
        lock (Gate)
        {
            var step = _current ?? _steps[0];
            step.State = "failed";
            step.Detail = detail;
            _problem = problem;
        }
    }

    sealed class StopSetup : Exception;

    static async Task RunAll()
    {
        try
        {
            // 1. Scan
            Begin("scan", "Reading graphics card, network and drivers…");
            var host = await Probe.Host(refresh: true);
            var nic = host["nic"];
            var link = nic == null ? "no network found" : $"{(host["wired"]!.GetValue<bool>() ? "wired" : "Wi-Fi")} {nic["speed"]}";
            End($"{host["gpu"]} · {link}");

            // 2. Install / admin work
            Begin("install");
            var needSunshine = !Sunshine.Installed;
            var needVigem = host["vigem"]?.GetValue<bool>() != true;
            var needService = !needSunshine && Sunshine.ServiceState() != "Running";
            var needFirewall = !needSunshine && host["firewall"]?.GetValue<bool>() != true;
            var needKitRule = host["kitFirewall"]?.GetValue<bool>() != true;

            if (needSunshine || needVigem || needService || needFirewall || needKitRule)
            {
                if ((needSunshine || needVigem) && host["winget"]?.GetValue<bool>() != true)
                {
                    Fail("Windows Package Manager (winget) is missing. Install \"App Installer\" from the Microsoft Store, then run setup again.");
                    throw new StopSetup();
                }
                Detail("Waiting for you to approve the Windows administrator prompt…");
                await RunAdmin(needSunshine, needVigem);
                Probe.Invalidate();
                host = await Probe.Host(refresh: true);
                if (!Sunshine.Installed)
                {
                    Fail("Sunshine did not install. Check your internet connection and run setup again.");
                    throw new StopSetup();
                }
                if (host["vigem"]?.GetValue<bool>() != true)
                {
                    Fail("The controller driver (ViGEmBus) did not install. Run setup again; if it keeps failing, restart Windows first.");
                    throw new StopSetup();
                }
                End($"Sunshine {Sunshine.Version()} and the ViGEmBus controller driver are in place");
            }
            else
            {
                End($"Already installed: Sunshine {Sunshine.Version()} and the ViGEmBus controller driver", "skipped");
            }

            // 3. Service
            Begin("service", "Waiting for Sunshine to come up…");
            if (!await Sunshine.WaitUntilUp(75))
            {
                Fail("The Sunshine service is not answering. Restart Windows and run setup again.");
                throw new StopSetup();
            }
            End("Running, and set to start with Windows");

            // 4. Login
            Begin("login");
            var status = await Sunshine.Status();
            if (status == ApiStatus.NoCredentials)
            {
                var pass = CredentialStore.NewPassword();
                await Sunshine.CreateLogin("solstice", pass);
                CredentialStore.Save("solstice", pass);
                for (var i = 0; i < 10 && await Sunshine.Status() != ApiStatus.Ok; i++) await Task.Delay(700);
                status = await Sunshine.Status();
            }
            if (status != ApiStatus.Ok)
            {
                Fail("Sunshine already has a login that Solstice does not know.", "need-login");
                throw new StopSetup();
            }
            End("Random 28-character password, stored encrypted for your Windows account");

            await Finish();
        }
        catch (StopSetup)
        {
            // The failing step has already been recorded.
        }
    }

    /// <summary>Steps that only need a working login. Also used to resume after a login is supplied.</summary>
    static async Task Finish()
    {
        // 5. Tune
        Begin("tune", "Writing settings and restarting Sunshine…");
        var result = await Tuning.Apply(Tuning.Current());
        var host = await Probe.Host();
        var d = host["display"];
        End(result == "pending"
            ? "Saved. They take effect when the current stream ends."
            : $"{ProfileName()} profile · display follows the device ({d?["w"]}×{d?["h"]} monitor)");

        // 6. Library
        Begin("library", "Looking for launchers…");
        var snap = await Library.Snapshot();
        var selected = new HashSet<string>();
        foreach (var it in snap["items"]!.AsArray().OfType<JsonObject>())
            if (it["kind"]!.ToString() == "launcher" || it["added"]!.GetValue<bool>())
                selected.Add(it["id"]!.ToString());
        await Library.Sync(selected);
        var launchers = snap["items"]!.AsArray().OfType<JsonObject>().Where(i => i["kind"]!.ToString() == "launcher").Select(i => i["source"]!.ToString()).ToArray();
        End(launchers.Length == 0 ? "No launchers found. Desktop streaming is ready." : "Desktop, " + string.Join(", ", launchers));

        // 7. Verify
        Begin("verify", "Testing the video encoder…");
        JsonObject act = Probe.Activity();
        for (var i = 0; i < 60 && act["encoders"]!.AsArray().Count == 0; i++)
        {
            await Task.Delay(1000);
            act = Probe.Activity();
        }
        var enc = act["encoders"]!.AsArray().Select(e => e!.ToString()).ToArray();
        if (enc.Length == 0)
        {
            Fail("Sunshine could not start a hardware video encoder. Update your graphics driver, then run setup again.");
            return;
        }
        if (act["gamepadMissing"]!.GetValue<bool>())
        {
            Fail("Sunshine cannot see the controller driver yet. Restart Windows, then run setup again.");
            return;
        }
        Settings.Update(s => s["setupDone"] = true);
        End($"Hardware encoding ready ({string.Join(", ", enc)}) · controller driver detected");
    }

    static string ProfileName() => Tuning.Current().Str("profile", "balanced") switch
    {
        "latency" => "Lowest latency",
        "picture" => "Best picture",
        _ => "Balanced",
    };

    /// <summary>Called after the user supplies (or resets) the Sunshine login for a stuck setup.</summary>
    public static bool Resume()
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _problem = null;
            foreach (var s in _steps.Where(s => s.Id == "login")) { s.State = "done"; s.Detail = "Login saved, stored encrypted for your Windows account"; }
        }
        _ = Task.Run(async () =>
        {
            try { await Finish(); }
            catch (Exception ex) { Fail(ex.Message); }
            finally { lock (Gate) _running = false; }
        });
        return true;
    }

    static async Task RunAdmin(bool installSunshine, bool installVigem)
    {
        var progress = Path.Combine(Paths.Run, "setup-progress.txt");
        File.WriteAllText(progress, "");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Continue'");
        sb.AppendLine($"$log={Proc.Q(progress)}");
        sb.AppendLine("function Say($m){ Add-Content -LiteralPath $log -Value $m -Encoding UTF8 }");
        sb.AppendLine("$fail=0");
        const string winget = "--exact --source winget --silent --disable-interactivity --accept-source-agreements --accept-package-agreements";
        if (installSunshine)
            sb.AppendLine($"Say 'Downloading and installing Sunshine…'; winget install --id LizardByte.Sunshine {winget} | Out-Null; if($LASTEXITCODE -ne 0){{ Say \"Sunshine installer returned $LASTEXITCODE\"; $fail=1 }}");
        if (installVigem)
            sb.AppendLine($"Say 'Installing the ViGEmBus controller driver…'; winget install --id ViGEm.ViGEmBus {winget} | Out-Null; if($LASTEXITCODE -ne 0){{ Say \"ViGEmBus installer returned $LASTEXITCODE\"; $fail=1 }}");
        sb.AppendLine("""
            $svc = Get-Service SunshineService -ErrorAction SilentlyContinue
            if ($svc) {
              Set-Service SunshineService -StartupType Automatic
              if ($svc.Status -ne 'Running') { Say 'Starting the streaming service…'; Start-Service SunshineService -ErrorAction SilentlyContinue }
              $exe = Join-Path (Split-Path (Split-Path ((Get-CimInstance Win32_Service -Filter "Name='SunshineService'").PathName -replace '"','') -Parent) -Parent) 'sunshine.exe'
              if ((Test-Path $exe) -and -not (Get-NetFirewallRule -DisplayName 'Sunshine' -ErrorAction SilentlyContinue)) {
                Say 'Opening the firewall for Sunshine…'
                New-NetFirewallRule -DisplayName 'Sunshine' -Direction Inbound -Action Allow -Program $exe -Protocol TCP -Profile Any | Out-Null
                New-NetFirewallRule -DisplayName 'Sunshine' -Direction Inbound -Action Allow -Program $exe -Protocol UDP -Profile Any | Out-Null
              }
            }
            """);
        sb.AppendLine($"if (-not (Get-NetFirewallRule -DisplayName {Proc.Q(KitRuleName)} -ErrorAction SilentlyContinue)) {{ Say 'Allowing device setup from your home network…'; New-NetFirewallRule -DisplayName {Proc.Q(KitRuleName)} -Description 'Lets devices on your local network fetch the Solstice setup script while you have the Add device screen open.' -Direction Inbound -Action Allow -Protocol TCP -LocalPort {DeviceLink.Port} -RemoteAddress LocalSubnet -Profile Any | Out-Null }}");
        sb.AppendLine("Say 'Finishing…'; exit $fail");

        var run = Proc.PowerShellElevated(sb.ToString());
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(700));
            try
            {
                var last = File.ReadLines(progress).LastOrDefault(l => l.Trim().Length > 0);
                if (last != null) Detail(last.Trim());
            }
            catch { /* file is being written */ }
        }
        var code = await run;
        if (code == null)
        {
            Fail("Administrator approval was declined, so nothing was installed. Run setup again and choose Yes.", "declined");
            throw new StopSetup();
        }
    }

    /// <summary>Replaces an unknown Sunshine login with a fresh one. Needs one administrator prompt.</summary>
    public static async Task<string?> ResetLogin()
    {
        var dir = Sunshine.InstallDir();
        if (dir == null) return "Sunshine is not installed.";
        var pass = CredentialStore.NewPassword();
        var script = $"& {Proc.Q(Path.Combine(dir, "sunshine.exe"))} --creds solstice {Proc.Q(pass)} | Out-Null; Restart-Service {Sunshine.ServiceName} -Force";
        var code = await Proc.PowerShellElevated(script, 120000);
        if (code == null) return "Administrator approval was declined.";
        CredentialStore.Save("solstice", pass);
        await Task.Delay(2000);
        await Sunshine.WaitUntilUp(60);
        for (var i = 0; i < 15 && await Sunshine.Status() != ApiStatus.Ok; i++) await Task.Delay(1000);
        return await Sunshine.Status() == ApiStatus.Ok ? null : "Sunshine did not accept the new login. Try again.";
    }
}
