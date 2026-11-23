using System.Net;
using System.Net.Http.Headers;
using System.ServiceProcess;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace Solstice;

enum ApiStatus
{
    Down,           // nothing answering on the web UI port
    NoCredentials,  // fresh install, no login has ever been set
    NeedLogin,      // a login exists but we do not have it (or ours is wrong)
    Ok,
}

/// <summary>Client for the local Sunshine service: files on disk, the Windows service, and its REST API.</summary>
static class Sunshine
{
    public const string ServiceName = "SunshineService";

    // The API only ever lives on loopback and uses a self-signed certificate Sunshine generates
    // at install time, so certificate validation is skipped for this one client.
    static readonly HttpClient Http = new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        AllowAutoRedirect = false,
        UseProxy = false,
    })
    { Timeout = TimeSpan.FromSeconds(8) };

    public static string? InstallDir()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            var image = (key?.GetValue("ImagePath") as string)?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(image))
            {
                var exe = image.Contains(".exe", StringComparison.OrdinalIgnoreCase)
                    ? image[..(image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4)].Trim('"')
                    : image;
                var dir = Path.GetDirectoryName(exe);              // ...\Sunshine\tools
                var root = dir != null ? Path.GetDirectoryName(dir) : null;
                if (root != null && File.Exists(Path.Combine(root, "sunshine.exe"))) return root;
            }
        }
        catch { /* fall back to the default location */ }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine");
        return File.Exists(Path.Combine(fallback, "sunshine.exe")) ? fallback : null;
    }

    public static bool Installed => InstallDir() != null;

    public static string? Version()
    {
        foreach (var hive in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            try
            {
                using var root = Registry.LocalMachine.OpenSubKey(hive);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    if ((k?.GetValue("DisplayName") as string) == "Sunshine")
                        return k.GetValue("DisplayVersion") as string;
                }
            }
            catch { /* keep looking */ }
        }
        return null;
    }

    public static string ConfigDir => Path.Combine(InstallDir() ?? "", "config");
    public static string LogFile => Path.Combine(ConfigDir, "sunshine.log");

    /// <summary>Base stream port from sunshine.conf (default 47989). The web UI is always +1.</summary>
    public static int BasePort()
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(ConfigDir, "sunshine.conf")))
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2 && parts[0].Trim() == "port" && int.TryParse(parts[1].Trim(), out var p)) return p;
            }
        }
        catch { /* default */ }
        return 47989;
    }

    public static string WebUiUrl => $"https://localhost:{BasePort() + 1}";
    static string Api => $"https://127.0.0.1:{BasePort() + 1}";

    public static string ServiceState()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status.ToString();
        }
        catch
        {
            return "Missing";
        }
    }

    static async Task<HttpResponseMessage> Send(HttpMethod method, string path, JsonNode? body = null,
        (string User, string Pass)? login = null, bool anonymous = false)
    {
        var req = new HttpRequestMessage(method, Api + path);
        if (!anonymous)
        {
            login ??= CredentialStore.Load();
            if (login is { } l)
                req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{l.User}:{l.Pass}")));
        }
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await Http.SendAsync(req);
    }

    static async Task<JsonNode?> Json(HttpMethod method, string path, JsonNode? body = null)
    {
        using var res = await Send(method, path, body);
        if (res.StatusCode == HttpStatusCode.Unauthorized) throw new AppException("Sunshine rejected the saved login.");
        if ((int)res.StatusCode is >= 300 and < 400) throw new AppException("Sunshine has no login set yet.");
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? node = null;
        try { node = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text); } catch { /* not JSON */ }
        if (!res.IsSuccessStatusCode)
            throw new AppException(node?["error"]?.ToString() ?? $"Sunshine returned HTTP {(int)res.StatusCode}.");
        return node;
    }

    public static async Task<ApiStatus> Status((string User, string Pass)? login = null)
    {
        try
        {
            using var res = await Send(HttpMethod.Get, "/api/config", login: login);
            if (res.StatusCode == HttpStatusCode.OK) return ApiStatus.Ok;
            if ((int)res.StatusCode is >= 300 and < 400) return ApiStatus.NoCredentials;
            return ApiStatus.NeedLogin;
        }
        catch
        {
            return ApiStatus.Down;
        }
    }

    public static async Task<bool> WaitUntilUp(int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await Status() != ApiStatus.Down) return true;
            await Task.Delay(800);
        }
        return false;
    }

    /// <summary>First-run only: Sunshine accepts a new login without authentication while none exists.</summary>
    public static async Task CreateLogin(string user, string pass)
    {
        using var res = await Send(HttpMethod.Post, "/api/password", new JsonObject
        {
            ["newUsername"] = user,
            ["newPassword"] = pass,
            ["confirmNewPassword"] = pass,
        }, anonymous: true);
        var text = await res.Content.ReadAsStringAsync();
        JsonNode? node = null;
        try { node = JsonNode.Parse(text); } catch { /* handled below */ }
        if (!res.IsSuccessStatusCode || node?["status"]?.GetValue<bool>() != true)
            throw new AppException(node?["error"]?.ToString() ?? "Sunshine did not accept the new login.");
    }

    public static async Task<JsonObject> GetConfig()
    {
        var o = (await Json(HttpMethod.Get, "/api/config"))!.AsObject();
        return o;
    }

    /// <summary>Keys the API adds to GET /api/config that are not settings.</summary>
    static readonly string[] MetaKeys = { "status", "platform", "version", "vigembus", "virtualhid", "restart_supported" };

    /// <summary>
    /// Sunshine rewrites its whole config from the posted keys, so changes are merged into what is
    /// already there. A null value removes the key, which returns that setting to Sunshine's default.
    /// </summary>
    public static async Task<bool> PatchConfig(IDictionary<string, string?> changes)
    {
        var current = await GetConfig();
        var next = new JsonObject();
        foreach (var (k, v) in current)
        {
            if (Array.IndexOf(MetaKeys, k) >= 0 || v is not JsonValue) continue;
            next[k] = v.ToString();
        }
        var changed = false;
        foreach (var (k, v) in changes)
        {
            var had = next[k]?.ToString();
            if (v == null)
            {
                if (had != null) { next.Remove(k); changed = true; }
            }
            else if (had != v)
            {
                next[k] = v;
                changed = true;
            }
        }
        if (changed) await Json(HttpMethod.Post, "/api/config", next);
        return changed;
    }

    public static async Task Restart()
    {
        try { using var _ = await Send(HttpMethod.Post, "/api/restart"); }
        catch { /* the service drops the connection while restarting */ }
    }

    public static async Task<JsonArray> GetApps() =>
        (await Json(HttpMethod.Get, "/api/apps"))?["apps"]?.AsArray() ?? new JsonArray();

    public static Task SaveApp(JsonObject app) => Json(HttpMethod.Post, "/api/apps", app);

    public static Task DeleteApp(int index) => Json(HttpMethod.Delete, $"/api/apps/{index}");

    public static async Task<JsonArray> GetClients() =>
        (await Json(HttpMethod.Get, "/api/clients/list"))?["named_certs"]?.AsArray() ?? new JsonArray();

    public static async Task<bool> Unpair(string uuid) =>
        (await Json(HttpMethod.Post, "/api/clients/unpair", new JsonObject { ["uuid"] = uuid }))?["status"]?.GetValue<bool>() == true;

    public static async Task<JsonArray> PendingPairings() =>
        (await Json(HttpMethod.Get, "/api/pin"))?["pairings"]?.AsArray() ?? new JsonArray();

    public static async Task<bool> SubmitPin(string pairingId, string pin, string name) =>
        (await Json(HttpMethod.Post, "/api/pin", new JsonObject
        {
            ["pairing_id"] = pairingId,
            ["pin"] = pin,
            ["name"] = name,
        }))?["status"]?.GetValue<bool>() == true;

    public static async Task CloseRunningApp()
    {
        await Json(HttpMethod.Post, "/api/apps/close");
    }

    /// <summary>Tail of the log, read straight from disk (the file is readable without admin rights).</summary>
    public static string[] LogTail(int maxLines)
    {
        try
        {
            using var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int window = 256 * 1024;
            if (fs.Length > window) fs.Seek(-window, SeekOrigin.End);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var lines = sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            return lines.Length > maxLines ? lines[^maxLines..] : lines;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}

