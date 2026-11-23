using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Solstice;

/// <summary>
/// "Add a device" helper. While the Add device panel is open this listens on the home network and:
///   - serves a small page from which the handheld downloads the Solstice Play app, and
///   - receives that app's pairing request, which the person at this PC approves with one click
///     after checking that both screens show the same four digits.
///
/// It is deliberately narrow: it runs only while requested (15 minutes at most), the firewall
/// rule admits the local subnet only, nothing it serves is secret, and no device is paired
/// without an explicit approval on this PC.
/// </summary>
static class DeviceLink
{
    public const int Port = 47800;
    const int MaxDeclined = 5;

    sealed class Request
    {
        public string Pin = "", Name = "", Remote = "";
        public readonly TaskCompletionSource<bool> Decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static readonly object Gate = new();
    static TcpListener? _listener;
    static CancellationTokenSource? _cts;
    static string _ip = "", _lastPaired = "", _note = "";
    static DateTime _expires;
    static Request? _request;
    static int _declined;

    public static string? ClientExe()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SOLSTICE_CLIENT_EXE"),
            Path.Combine(AppContext.BaseDirectory, "client", "SolsticePlay.exe"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    public static JsonObject Status()
    {
        lock (Gate)
        {
            if (_listener != null && DateTime.UtcNow >= _expires) StopLocked();
            var active = _listener != null;
            return new JsonObject
            {
                ["active"] = active,
                ["url"] = active ? $"http://{_ip}:{Port}" : null,
                ["secondsLeft"] = active ? (int)(_expires - DateTime.UtcNow).TotalSeconds : 0,
                ["clientAvailable"] = ClientExe() != null,
                ["request"] = _request == null ? null : new JsonObject { ["pin"] = _request.Pin, ["name"] = _request.Name, ["address"] = _request.Remote },
                ["lastPaired"] = _lastPaired,
                ["note"] = _note,
            };
        }
    }

    public static async Task<JsonObject> Start()
    {
        var host = await Probe.Host();
        var ip = host["ip"]?.ToString();
        if (string.IsNullOrEmpty(ip)) throw new AppException("This PC has no network address right now.");
        if (host["kitFirewall"]?.GetValue<bool>() != true)
        {
            // Normally created during setup; create it now if it was removed.
            var rule = $"New-NetFirewallRule -DisplayName {Proc.Q(Setup.KitRuleName)} -Direction Inbound -Action Allow -Protocol TCP -LocalPort {Port} -RemoteAddress LocalSubnet -Profile Any | Out-Null";
            if (await Proc.PowerShellElevated(rule, 60000) == null)
                throw new AppException("Administrator approval is needed once to let devices on your network reach this PC.");
            Probe.Invalidate();
        }

        lock (Gate)
        {
            if (_listener != null && _ip == ip)
            {
                _expires = DateTime.UtcNow.AddMinutes(15);
                return Status();
            }
            StopLocked();
            _ip = ip;
            _expires = DateTime.UtcNow.AddMinutes(15);
            _declined = 0;
            _lastPaired = _note = "";
            _cts = new CancellationTokenSource();
            try
            {
                _listener = new TcpListener(IPAddress.Parse(ip), Port);
                _listener.Start();
            }
            catch (SocketException)
            {
                _listener = null;
                throw new AppException($"Port {Port} is in use by another program, so device setup cannot start.");
            }
            _ = AcceptLoop(_listener, _cts.Token);
        }
        return Status();
    }

    public static void Stop()
    {
        lock (Gate) StopLocked();
    }

    static void StopLocked()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        _listener = null;
        _cts = null;
        _request?.Decision.TrySetResult(false);
        _request = null;
    }

    /// <summary>The person at this PC answered the pairing prompt.</summary>
    public static void Decide(bool approve)
    {
        lock (Gate)
        {
            if (_request == null) return;
            _request.Decision.TrySetResult(approve);
            if (!approve && ++_declined >= MaxDeclined)
            {
                _note = "Device setup was closed after several declined requests.";
                StopLocked();
            }
        }
    }

    static async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Handle(client), ct);
        }
    }

    static async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buf = new byte[8192];
                var len = 0;
                while (len < buf.Length)
                {
                    using var cts = new CancellationTokenSource(8000);
                    var n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), cts.Token);
                    if (n <= 0) break;
                    len += n;
                    if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                }
                var first = Encoding.ASCII.GetString(buf, 0, len).Split("\r\n")[0].Split(' ');
                if (first.Length < 2 || first[0] != "GET")
                {
                    await Reply(stream, 405, "text/plain", "Method not allowed");
                    return;
                }
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                await Route(stream, first[1], remote);
            }
            catch { /* dropped connection */ }
        }
    }

    static string Head(int status, string type, long length, string? extra = null)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", 429 => "Too Many Requests", _ => "Error" };
        return $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n{extra}\r\n";
    }

    static async Task Reply(NetworkStream s, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await s.WriteAsync(Encoding.ASCII.GetBytes(Head(status, type + "; charset=utf-8", bytes.Length)));
        await s.WriteAsync(bytes);
        await s.FlushAsync();
    }

    static async Task Route(NetworkStream s, string target, string remote)
    {
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        var query = q < 0 ? "" : target[(q + 1)..];

        switch (path)
        {
            case "/":
                await Reply(s, 200, "text/html", PageTemplate.Replace("__HOST__", WebUtility.HtmlEncode(Environment.MachineName)));
                return;

            case "/hello":
                await Reply(s, 200, "application/json", new JsonObject
                {
                    ["app"] = "solstice", ["name"] = Environment.MachineName, ["version"] = AppInfo.Version,
                }.ToJsonString());
                return;

            case "/SolsticePlay.exe":
            {
                var exe = ClientExe();
                if (exe == null) { await Reply(s, 404, "text/plain", "The handheld app is not bundled with this install."); return; }
                await using var f = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                await s.WriteAsync(Encoding.ASCII.GetBytes(Head(200, "application/octet-stream", f.Length,
                    "Content-Disposition: attachment; filename=\"SolsticePlay.exe\"\r\n")));
                await f.CopyToAsync(s);
                await s.FlushAsync();
                return;
            }

            case "/pair":
            {
                var pin = Param(query, "pin");
                var name = Clean(Param(query, "name"));
                if (!Regex.IsMatch(pin, @"^\d{4}$")) { await Reply(s, 200, "application/json", "{\"status\":false,\"reason\":\"bad-pin\"}"); return; }
                if (name.Length == 0) name = "Handheld";

                Request? req = null;
                lock (Gate)
                {
                    // One request at a time, so the prompt on the PC is never ambiguous.
                    if (_listener != null && _request == null)
                        req = _request = new Request { Pin = pin, Name = name, Remote = remote };
                }
                if (req == null)
                {
                    await Reply(s, 429, "application/json", "{\"status\":false,\"reason\":\"busy\"}");
                    return;
                }

                bool approved;
                try { approved = await req.Decision.Task.WaitAsync(TimeSpan.FromSeconds(120)); }
                catch (TimeoutException) { approved = false; }

                var ok = false;
                if (approved) ok = await Pair(pin, name, remote);
                lock (Gate)
                {
                    if (_request == req) _request = null;
                    if (ok) _lastPaired = name;
                }
                await Reply(s, 200, "application/json", new JsonObject
                {
                    ["status"] = ok,
                    ["reason"] = ok ? "paired" : approved ? "pin-rejected" : "declined",
                }.ToJsonString());
                return;
            }
        }
        await Reply(s, 404, "text/plain", "Not found");
    }

    static string Param(string query, string key)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv[0] == key) return Uri.UnescapeDataString(kv.Length > 1 ? kv[1].Replace('+', ' ') : "");
        }
        return "";
    }

    static string Clean(string s)
    {
        s = Regex.Replace(s, @"[^\w \-\.\(\)]", "").Trim();
        return s.Length > 40 ? s[..40] : s;
    }

    /// <summary>Waits for the device's pairing request to reach Sunshine, then answers it with the PIN.</summary>
    public static async Task<bool> Pair(string pin, string name, string remote)
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                var pending = await Sunshine.PendingPairings();
                if (pending.Count > 0)
                {
                    // Prefer the request that came from the same address as the approval request.
                    var pick = pending.OfType<JsonObject>().FirstOrDefault(p => p.ToJsonString().Contains($"\"{remote}\""))
                               ?? pending.OfType<JsonObject>().Last();
                    var id = pick["id"]?.ToString();
                    if (id != null) return await Sunshine.SubmitPin(id, pin, name);
                }
            }
            catch { /* Sunshine busy; retry */ }
            await Task.Delay(500);
        }
        return false;
    }

    const string PageTemplate = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Solstice Play</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; padding: 28px;
                 background: #0d0f14; color: #eceef2; font: 18px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
          main { width: 100%; max-width: 580px; }
          .mark { width: 48px; height: 48px; border-radius: 50%; margin-bottom: 22px;
                  background: radial-gradient(circle at 68% 34%, #0d0f14 0 30%, transparent 31%), linear-gradient(135deg, #ffb347, #ff8a3d); }
          h1 { font-size: 32px; line-height: 1.15; margin: 0 0 10px; font-weight: 600; letter-spacing: -0.015em; text-wrap: balance; }
          p { margin: 0 0 22px; color: #a4abb8; text-wrap: pretty; }
          a.btn { display: block; padding: 18px 20px; border-radius: 14px; font-weight: 600; text-align: center;
                  text-decoration: none; background: #ffb347; color: #1a1205; }
          a.btn:focus-visible { outline: 3px solid #8b93ff; outline-offset: 3px; }
          ol { padding-left: 24px; color: #c9ced8; margin: 26px 0 0; }
          li { margin-bottom: 10px; }
          b { color: #eceef2; font-weight: 600; }
        </style></head>
        <body><main>
          <div class="mark" aria-hidden="true"></div>
          <h1>Play your __HOST__ games on this device</h1>
          <p>Solstice Play sets everything up here: it installs Moonlight, finds your PC, pairs with it and picks the best settings for this screen.</p>
          <a class="btn" href="/SolsticePlay.exe" download>Download Solstice Play</a>
          <ol>
            <li>Open the downloaded <b>SolsticePlay.exe</b>.</li>
            <li>If Windows shows a blue "protected your PC" box, choose <b>More info</b>, then <b>Run anyway</b>. The app came from your own PC and is not signed by a store.</li>
            <li>Tap <b>Set up</b>, then approve the request that appears on your PC.</li>
          </ol>
        </main></body></html>
        """;
}
