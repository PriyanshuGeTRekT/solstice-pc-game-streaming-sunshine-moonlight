using System.Collections.Specialized;
using System.Text.Json.Nodes;
using static Solstice.WebHost;

namespace Solstice;

/// <summary>Everything the handheld app's UI can ask for.</summary>
static class PlayApi
{
    public static async Task<JsonNode> Handle(string method, string path, JsonObject body, NameValueCollection query)
    {
        switch (method + " " + path)
        {
            case "GET /api/state":
                return await State();

            case "POST /api/setup/start":
            {
                var host = body.Str("host", "").Trim();
                Play.StartSetup(host.Length > 0 ? host : null);
                return Ok();
            }

            case "GET /api/apps":
            {
                var apps = await Play.Apps(query["refresh"] == "1");
                return Ok(o => o["apps"] = new JsonArray(apps.Select(a => (JsonNode)a).ToArray()));
            }

            case "POST /api/play":
            {
                var app = body.Str("app", "").Trim();
                if (app.Length == 0) return Err("Choose something to play.");
                await Play.Launch(app);
                return Ok();
            }

            case "POST /api/wake":
            {
                var s = Settings.Load();
                if (s.Str("hostMac", "").Length == 0) return Err("This device has not learned the PC's hardware address yet. Wake the PC by hand once and run setup again.");
                return await Play.EnsureAwake(75) ? Ok() : Err("The PC did not wake. It may be switched off fully, or Wake-on-LAN may be disabled in its firmware.");
            }

            case "POST /api/host/quit":
                await Play.QuitOnHost();
                return Ok();

            case "POST /api/settings":
            {
                var (_, _, hz) = Play.Screen();
                Settings.Update(s =>
                {
                    var res = body.Str("resolution", "native");
                    s["resolution"] = res is "720p" or "1080p" ? res : "native";
                    var fps = body.Int("fps", 0);
                    s["fps"] = fps is 30 or 60 or 90 or 120 && fps <= Math.Max(hz, 60) ? fps : 0;
                    var mbps = body.Int("bitrateMbps", 0);
                    s["bitrateMbps"] = mbps <= 0 ? 0 : Math.Clamp(mbps, 5, 150);
                    var codec = body.Str("codec", "HEVC");
                    s["codec"] = codec is "H.264" or "AV1" ? codec : "HEVC";
                    foreach (var key in new[] { "vsync", "hdr", "overlay" }) s[key] = body.Bool(key, false);
                    s["quitAfter"] = body.Bool("quitAfter", true);
                });
                Moonlight.SeedSettings(Play.Stream());
                return Ok(o => o["stream"] = StreamJson());
            }

            case "POST /api/forget":
                Settings.Update(s => { s.Remove("hostIp"); s.Remove("hostName"); s.Remove("hostMac"); s.Remove("apps"); s["setupDone"] = false; });
                return Ok();

            case "POST /api/open":
                switch (body.Str("what", ""))
                {
                    case "moonlight-site": Proc.OpenExternal("https://moonlight-stream.org"); break;
                    case "moonlight":
                        if (Moonlight.Exe() != null) Moonlight.Start(Array.Empty<string>());
                        break;
                }
                return Ok();
        }
        return Err("Unknown request.");
    }

    static JsonObject StreamJson()
    {
        var s = Settings.Load();
        var st = Play.Stream();
        var (sw, sh, shz) = Play.Screen();
        return new JsonObject
        {
            ["width"] = st.Width, ["height"] = st.Height, ["fps"] = st.Fps, ["mbps"] = st.BitrateKbps / 1000,
            ["codec"] = st.Codec, ["vsync"] = st.VSync, ["hdr"] = st.Hdr, ["overlay"] = st.Overlay, ["quitAfter"] = st.QuitAfter,
            // The stored choices (0 / "native" mean "decide for me").
            ["resolution"] = s.Str("resolution", "native"),
            ["fpsChoice"] = s.Int("fps", 0),
            ["bitrateChoice"] = s.Int("bitrateMbps", 0),
            ["autoMbps"] = Play.AutoBitrateKbps(st.Width, st.Height, st.Fps) / 1000,
            ["screen"] = new JsonObject { ["w"] = sw, ["h"] = sh, ["hz"] = shz },
        };
    }

    static async Task<JsonObject> State()
    {
        var s = Settings.Load();
        var ip = s.Str("hostIp", "");
        var online = ip.Length > 0 && await Play.Online(ip);
        return new JsonObject
        {
            ["version"] = AppInfo.Version,
            ["device"] = Environment.MachineName,
            ["moonlight"] = new JsonObject { ["installed"] = Moonlight.Exe() != null, ["version"] = Moonlight.Version() },
            ["host"] = ip.Length == 0 ? null : new JsonObject
            {
                ["ip"] = ip, ["name"] = s.Str("hostName", ip), ["online"] = online, ["canWake"] = s.Str("hostMac", "").Length > 0,
            },
            ["setupDone"] = s.Bool("setupDone", false) && ip.Length > 0 && Moonlight.Exe() != null,
            ["setup"] = Play.SetupStatus(),
            ["stream"] = StreamJson(),
            ["playing"] = Play.Playing,
        };
    }
}
