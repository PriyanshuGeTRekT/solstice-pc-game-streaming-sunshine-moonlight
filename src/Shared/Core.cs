using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Solstice;

/// <summary>Set once at startup by each app (PC host app and handheld app).</summary>
static class AppInfo
{
    public static string Name = "Solstice";
    public static string Title = "Solstice";
    public const string Version = "1.0.2";
}

/// <summary>An error whose message is written for the person using the app.</summary>
class AppException(string message) : Exception(message);

/// <summary>Per-user data locations. Nothing here needs admin rights.</summary>
static class Paths
{
    public static string Data => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.Name));
    /// <summary>
    /// Box art lives in ProgramData rather than the user profile: the streaming service runs as
    /// SYSTEM and must be able to read it, and ProgramData is never redirected for packaged callers.
    /// </summary>
    public static string Covers => Ensure(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppInfo.Name, "covers"));
    public static string Run => Ensure(Path.Combine(Data, "run"));
    public static string WebView => Ensure(Path.Combine(Data, "webview"));
    public static string SettingsFile => Path.Combine(Data, "settings.json");

    static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

record ProcResult(int Code, string Out, string Err);

static class Proc
{
    public static async Task<ProcResult> Run(string file, IEnumerable<string> args, int timeoutMs = 30000, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDir != null) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* already gone */ }
            return new ProcResult(-1, "", "timed out");
        }
        return new ProcResult(p.ExitCode, await so, await se);
    }

    static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(
        "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));

    /// <summary>Runs a Windows PowerShell script as the current (non-elevated) user.</summary>
    public static Task<ProcResult> PowerShell(string script, int timeoutMs = 30000) =>
        Run("powershell.exe", new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Encode(script),
        }, timeoutMs);

    /// <summary>
    /// Runs a script elevated (one UAC prompt). The script travels on the command line, never as a
    /// file on disk, so nothing in a user-writable folder is executed with admin rights.
    /// Returns null when the user declines the prompt.
    /// </summary>
    public static async Task<int?> PowerShellElevated(string script, int timeoutMs = 900000)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand " + Encode(script),
        };
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return null;
        }
        if (p == null) return -1;
        using (p)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { return -1; }
            return p.ExitCode;
        }
    }

    public static void OpenExternal(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { /* nothing useful to do */ }
    }

    /// <summary>Single-quotes a value for embedding in a PowerShell script.</summary>
    public static string Q(string s) => "'" + s.Replace("'", "''") + "'";
}

/// <summary>App preferences, stored as JSON in the per-user data folder.</summary>
static class Settings
{
    static readonly object Gate = new();

    public static JsonObject Load()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(Paths.SettingsFile))
                    return JsonNode.Parse(File.ReadAllText(Paths.SettingsFile))!.AsObject();
            }
            catch { /* fall through to defaults */ }
            return new JsonObject();
        }
    }

    public static void Update(Action<JsonObject> change)
    {
        lock (Gate)
        {
            JsonObject o;
            try
            {
                o = File.Exists(Paths.SettingsFile)
                    ? JsonNode.Parse(File.ReadAllText(Paths.SettingsFile))!.AsObject()
                    : new JsonObject();
            }
            catch { o = new JsonObject(); }
            change(o);
            File.WriteAllText(Paths.SettingsFile, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public static string Str(this JsonObject o, string key, string fallback) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    public static bool Bool(this JsonObject o, string key, bool fallback) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public static int Int(this JsonObject o, string key, int fallback) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;
}

/// <summary>Tiny step tracker shared by both apps' guided setup flows.</summary>
sealed class StepList
{
    public sealed class Step(string id, string label)
    {
        public string Id = id, Label = label, State = "pending", Detail = "";
    }

    readonly object _gate = new();
    List<Step> _steps = new();
    Step? _current;
    public bool Running { get; private set; }
    public string? Problem { get; private set; }

    public void Reset(params (string Id, string Label)[] steps)
    {
        lock (_gate)
        {
            _steps = steps.Select(s => new Step(s.Id, s.Label)).ToList();
            _current = null;
            Problem = null;
        }
    }

    public bool TryStart()
    {
        lock (_gate)
        {
            if (Running) return false;
            Running = true;
            Problem = null;
            return true;
        }
    }

    public void Stop() { lock (_gate) Running = false; }

    public void Begin(string id, string detail = "")
    {
        lock (_gate)
        {
            _current = _steps.First(s => s.Id == id);
            _current.State = "running";
            _current.Detail = detail;
        }
    }

    public void Detail(string detail) { lock (_gate) if (_current != null) _current.Detail = detail; }

    public void End(string detail, string state = "done")
    {
        lock (_gate)
        {
            if (_current == null) return;
            _current.State = state;
            _current.Detail = detail;
        }
    }

    public void Mark(string id, string state, string detail)
    {
        lock (_gate)
        {
            var s = _steps.FirstOrDefault(x => x.Id == id);
            if (s == null) return;
            s.State = state;
            s.Detail = detail;
        }
    }

    public void Fail(string detail, string problem = "failed")
    {
        lock (_gate)
        {
            var step = _current ?? _steps.FirstOrDefault();
            if (step != null) { step.State = "failed"; step.Detail = detail; }
            Problem = problem;
        }
    }

    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            var arr = new JsonArray();
            foreach (var s in _steps)
                arr.Add(new JsonObject { ["id"] = s.Id, ["label"] = s.Label, ["state"] = s.State, ["detail"] = s.Detail });
            return new JsonObject
            {
                ["running"] = Running,
                ["problem"] = Problem,
                ["done"] = !Running && Problem == null && _steps.Count > 0 && _steps.All(s => s.State is "done" or "skipped"),
                ["steps"] = arr,
            };
        }
    }
}
