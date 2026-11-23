using System.Collections.Specialized;
using System.Text.Json.Nodes;
using static Solstice.WebHost;

namespace Solstice;

/// <summary>
/// Simulated data for screenshots (run with <c>--demo [scene]</c>). Nothing real is read or changed.
/// Scenes: welcome, setup, pair, home, asleep, playing.
/// </summary>
static class Demo
{
    public static string Scene = "home";

    static readonly string[] Apps =
    {
        "Desktop", "Steam Big Picture", "Starfall Odyssey", "Neon Drift", "Skyline Racers",
        "Ember Knights Saga", "Epic Games Launcher", "Xbox app",
    };

    public static Task<JsonNode> Handle(string method, string path, JsonObject body, NameValueCollection query)
    {
        JsonNode r = (method + " " + path) switch
        {
            "GET /api/state" => State(),
            "GET /api/apps" => Ok(o => o["apps"] = new JsonArray(Apps.Select(a => (JsonNode)a).ToArray())),
            "POST /api/settings" => Ok(o => o["stream"] = Stream()),
            _ => Ok(),
        };
        return Task.FromResult(r);
    }

    static JsonObject Stream() => new()
    {
        ["width"] = 1920, ["height"] = 1080, ["fps"] = 120, ["mbps"] = 50, ["codec"] = "HEVC",
        ["vsync"] = false, ["hdr"] = false, ["overlay"] = false, ["quitAfter"] = true,
        ["resolution"] = "native", ["fpsChoice"] = 0, ["bitrateChoice"] = 0, ["autoMbps"] = 50,
        ["screen"] = new JsonObject { ["w"] = 1920, ["h"] = 1080, ["hz"] = 120 },
    };

    static JsonObject State()
    {
        var setup = Scene is "welcome" or "setup" or "pair";
        var steps = new JsonArray();
        (string Id, string Label, string Done)[] all =
        {
            ("moonlight", "Install Moonlight", "Moonlight 6.2.0 installed"),
            ("find", "Find your PC", "GAMING-PC at 192.168.1.50"),
            ("pair", "Pair with your PC", "Paired with GAMING-PC"),
            ("tune", "Tune for this screen", "1920×1080 at 120 fps · 50 Mbps · HEVC"),
            ("finish", "Add to the Start menu", "Solstice Play is in the Start menu and on the desktop"),
        };
        var doneCount = Scene switch { "setup" => 1, "pair" => 2, _ => -1 };
        for (var i = 0; i < all.Length; i++)
        {
            var state = doneCount < 0 ? "pending" : i < doneCount ? "done" : i == doneCount ? "running" : "pending";
            var detail = state == "done" ? all[i].Done
                : state == "running" ? (Scene == "pair" ? "Approve this device on GAMING-PC" : "Looking for your PC on this network…") : "";
            steps.Add(new JsonObject { ["id"] = all[i].Id, ["label"] = all[i].Label, ["state"] = state, ["detail"] = detail });
        }

        return new JsonObject
        {
            ["version"] = AppInfo.Version,
            ["device"] = "ROG-ALLY",
            ["moonlight"] = new JsonObject { ["installed"] = true, ["version"] = "6.2.0.0" },
            ["host"] = setup ? null : new JsonObject
            {
                ["ip"] = "192.168.1.50", ["name"] = "GAMING-PC", ["online"] = Scene != "asleep", ["canWake"] = true,
            },
            ["setupDone"] = !setup,
            ["setup"] = new JsonObject
            {
                ["running"] = Scene is "setup" or "pair", ["problem"] = null, ["done"] = !setup, ["steps"] = steps,
                ["hosts"] = new JsonArray(), ["pin"] = Scene == "pair" ? "4826" : "", ["pairMode"] = Scene == "pair" ? "approve" : "",
            },
            ["stream"] = Stream(),
            ["playing"] = Scene == "playing",
        };
    }
}
