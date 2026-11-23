using System.Collections.Specialized;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Solstice;

/// <summary>
/// Loopback-only HTTP server that backs the window's UI. It is bound to 127.0.0.1 on a random
/// port and every request must carry the per-launch session cookie, so other programs and web
/// pages on this PC cannot drive it.
/// </summary>
sealed class WebHost
{
    public delegate Task<JsonNode> ApiHandler(string method, string path, JsonObject body, NameValueCollection query);

    readonly HttpListener _listener = new();
    readonly ApiHandler _api;
    readonly Func<string, string?>? _files;
    readonly string? _webRoot;
    public int Port { get; }
    public string Token { get; }
    public string StartUrl => $"http://127.0.0.1:{Port}/?t={Token}";

    /// <param name="api">Handles /api/* requests.</param>
    /// <param name="files">Maps /file/{key} to a local file to serve (images), or null.</param>
    /// <param name="webRoot">Serve UI files from this folder instead of embedded resources (development).</param>
    public WebHost(ApiHandler api, Func<string, string?>? files = null, int port = 0, string? token = null, string? webRoot = null)
    {
        _api = api;
        _files = files;
        Port = port > 0 ? port : FreePort();
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _webRoot = webRoot;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => Handle(ctx));
            }
        });
    }

    public void Stop()
    {
        try { _listener.Stop(); } catch { /* shutting down */ }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";

            if (req.UserHostName != $"127.0.0.1:{Port}")
            {
                await Text(res, 403, "Forbidden");
                return;
            }

            var path = req.Url!.AbsolutePath;

            // First navigation carries the launch token; trade it for a cookie and drop it from the URL.
            if (path == "/" && req.QueryString["t"] is { } t)
            {
                if (!Same(t, Token)) { await Text(res, 403, "Forbidden"); return; }
                res.Headers.Add("Set-Cookie", $"solstice={Token}; Path=/; HttpOnly; SameSite=Strict");
                res.Redirect("/");
                res.Close();
                return;
            }

            if (!Same(req.Cookies["solstice"]?.Value ?? "", Token))
            {
                await Text(res, 403, $"Open {AppInfo.Title} from the Start menu.");
                return;
            }

            if (path.StartsWith("/api/"))
            {
                // A custom header cannot be sent cross-site without a CORS preflight, which is never granted.
                if (req.HttpMethod != "GET" && req.Headers["X-Solstice"] != "1")
                {
                    await Text(res, 403, "Forbidden");
                    return;
                }
                JsonNode? body = null;
                if (req.HasEntityBody)
                {
                    using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                    var raw = await sr.ReadToEndAsync();
                    if (raw.Length > 0) body = JsonNode.Parse(raw);
                }
                JsonNode result;
                try
                {
                    result = await _api(req.HttpMethod, path, body as JsonObject ?? new JsonObject(), req.QueryString);
                }
                catch (AppException ex)
                {
                    result = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
                }
                catch (HttpRequestException)
                {
                    result = new JsonObject { ["ok"] = false, ["error"] = "The streaming service is not answering. Try again in a few seconds." };
                }
                catch (TaskCanceledException)
                {
                    result = new JsonObject { ["ok"] = false, ["error"] = "That took too long. Try again in a few seconds." };
                }
                await Bytes(res, 200, "application/json", Encoding.UTF8.GetBytes(result.ToJsonString()));
                return;
            }

            if (path.StartsWith("/file/") && _files != null)
            {
                var file = _files(Uri.UnescapeDataString(path["/file/".Length..]));
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) { await Text(res, 404, "Not found"); return; }
                res.Headers["Cache-Control"] = "private, max-age=86400";
                await Bytes(res, 200, "image/png", await File.ReadAllBytesAsync(file));
                return;
            }

            await Static(res, path == "/" ? "/index.html" : path);
        }
        catch (Exception ex)
        {
            try { await Text(res, 500, ex.Message); } catch { /* client went away */ }
        }
    }

    static bool Same(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    static Task Text(HttpListenerResponse res, int status, string text) =>
        Bytes(res, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    static async Task Bytes(HttpListenerResponse res, int status, string type, byte[] data)
    {
        res.StatusCode = status;
        res.ContentType = type;
        res.ContentLength64 = data.Length;
        await res.OutputStream.WriteAsync(data);
        res.Close();
    }

    async Task Static(HttpListenerResponse res, string path)
    {
        var name = path.TrimStart('/');
        if (name.Contains("..") || name.Contains('\\')) { await Text(res, 404, "Not found"); return; }
        byte[]? data = null;
        if (_webRoot != null)
        {
            var file = Path.Combine(_webRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(file)) data = await File.ReadAllBytesAsync(file);
        }
        else
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("wwwroot/" + name);
            if (s != null)
            {
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                data = ms.ToArray();
            }
        }
        if (data == null) { await Text(res, 404, "Not found"); return; }
        var type = Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".ico" => "image/x-icon",
            _ => "application/octet-stream",
        };
        await Bytes(res, 200, type, data);
    }

    public static JsonObject Ok(Action<JsonObject>? fill = null)
    {
        var o = new JsonObject { ["ok"] = true };
        fill?.Invoke(o);
        return o;
    }

    public static JsonObject Err(string message) => new() { ["ok"] = false, ["error"] = message };
}
