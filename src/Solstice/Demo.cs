using System.Collections.Specialized;
using System.Text.Json.Nodes;
using static Solstice.WebHost;

namespace Solstice;

/// <summary>
/// Simulated data for screenshots and the website (run with <c>--demo [scene]</c>). Nothing here
/// touches Sunshine, the network or the real library, so no personal details appear in images.
/// Scenes: welcome, setup-0 … setup-6, home, streaming, link, approve, library, tuning.
/// </summary>
static class Demo
{
    public static string Scene = "home";

    static readonly (string Id, string Label, string Detail)[] SetupSteps =
    {
        ("scan", "Check this PC", "NVIDIA GeForce RTX 4070 · wired 1 Gbps"),
        ("install", "Install the streaming engine and controller driver", "Sunshine 2026.914.233613 and the ViGEmBus controller driver are in place"),
        ("service", "Start the streaming service", "Running, and set to start with Windows"),
        ("login", "Secure it with a private login", "Random 28-character password, stored encrypted for your Windows account"),
        ("tune", "Apply tuned settings", "Balanced profile · display follows the device (2560×1440 monitor)"),
        ("library", "Publish your launchers", "Desktop, Steam, Epic Games, EA, Ubisoft, Xbox"),
        ("verify", "Final check", "Hardware encoding ready (H.264, HEVC, AV1) · controller driver detected"),
    };

    static readonly (string Detail, string Running)[] RunningText =
    {
        ("", "Reading graphics card, network and drivers…"),
        ("", "Downloading and installing Sunshine…"),
        ("", "Waiting for Sunshine to come up…"),
        ("", ""),
        ("", "Writing settings and restarting Sunshine…"),
        ("", "Looking for launchers…"),
        ("", "Testing the video encoder…"),
    };

    static readonly LibItem[] Items =
    {
        new("launcher:steam", "Steam", "launcher", "Steam Big Picture", ""),
        new("launcher:epic", "Epic Games", "launcher", "Epic Games Launcher", ""),
        new("launcher:ea", "EA", "launcher", "EA app", ""),
        new("launcher:ubisoft", "Ubisoft", "launcher", "Ubisoft Connect", ""),
        new("launcher:xbox", "Xbox", "launcher", "Xbox app", ""),
        new("steam:1", "Steam", "game", "Starfall Odyssey", ""),
        new("steam:2", "Steam", "game", "Neon Drift", ""),
        new("steam:3", "Steam", "game", "Ironclad Tactics", ""),
        new("steam:4", "Steam", "game", "Hollow Pines", ""),
        new("epic:5", "Epic Games", "game", "Skyline Racers", ""),
        new("steam:6", "Steam", "game", "Ember Knights Saga", ""),
        new("epic:7", "Epic Games", "game", "Deep Current", ""),
        new("steam:8", "Steam", "game", "Paper Kingdoms", ""),
        new("steam:9", "Steam", "game", "Quiet Orbit", ""),
        new("steam:10", "Steam", "game", "Rust & Rain", ""),
    };

    static readonly HashSet<string> Added = new()
    {
        "launcher:steam", "launcher:epic", "launcher:ea", "launcher:ubisoft", "launcher:xbox",
        "steam:1", "steam:2", "epic:5", "steam:6",
    };

    public static string? File(string key)
    {
        if (!key.StartsWith("cover/")) return null;
        var item = Items.FirstOrDefault(i => i.Id == key["cover/".Length..]);
        if (item == null) return null;
        var dir = Path.Combine(Path.GetTempPath(), "SolsticeDemoCovers");
        Directory.CreateDirectory(dir);
        return Library.Render(item, Path.Combine(dir, item.Id.Replace(':', '_') + ".png"));
    }

    public static Task<JsonNode> Handle(string method, string path, JsonObject body, NameValueCollection query)
    {
        JsonNode r = (method + " " + path) switch
        {
            "GET /api/state" => State(),
            "GET /api/setup/status" => State()["setup"]!.DeepClone(),
            "GET /api/devices" => Ok(o =>
            {
                o["clients"] = new JsonArray(
                    new JsonObject { ["name"] = "ROG Ally", ["uuid"] = "demo-1" },
                    new JsonObject { ["name"] = "Living room TV", ["uuid"] = "demo-2" });
                o["pending"] = new JsonArray();
                o["link"] = Link();
            }),
            "GET /api/link" => Ok(o => o["link"] = Link()),
            "GET /api/library" => Ok(o =>
            {
                o["items"] = new JsonArray(Items.Select(i => (JsonNode)new JsonObject
                {
                    ["id"] = i.Id, ["source"] = i.Source, ["kind"] = i.Kind, ["name"] = i.Name, ["added"] = Added.Contains(i.Id),
                }).ToArray());
                o["other"] = new JsonArray("Desktop");
            }),
            "GET /api/tuning" => Ok(o => o["tuning"] = Tuning()),
            "GET /api/logs" => Ok(o => o["lines"] = new JsonArray(
                "[2026-11-23 20:14:02.118]: Info: Sunshine version: 2026.914.233613",
                "[2026-11-23 20:14:09.554]: Info: Found H.264 encoder: h264_nvenc [nvenc]",
                "[2026-11-23 20:14:09.554]: Info: Found HEVC encoder: hevc_nvenc [nvenc]",
                "[2026-11-23 20:14:09.554]: Info: Found AV1 encoder: av1_nvenc [nvenc]",
                "[2026-11-23 20:14:09.556]: Info: Configuration UI available at [https://localhost:47990]",
                "[2026-11-23 20:31:40.903]: Info: Display device configuration applied successfully.",
                "[2026-11-23 20:31:41.166]: Info: Executing [Steam Big Picture]",
                "[2026-11-23 20:31:41.370]: Info: CLIENT CONNECTED")),
            _ => Ok(),
        };
        return Task.FromResult(r);
    }

    static JsonObject Tuning() => new()
    {
        ["profile"] = "balanced", ["controller"] = "auto", ["matchDisplay"] = true, ["lockWebUi"] = true, ["guideHold"] = true, ["hideTray"] = true,
    };

    static JsonObject Link() => Scene == "link"
        ? new JsonObject
        {
            ["active"] = true, ["url"] = "http://192.168.1.50:47800", ["secondsLeft"] = 878, ["clientAvailable"] = true,
            ["request"] = null, ["lastPaired"] = "", ["note"] = "",
        }
        : Scene == "approve"
        ? new JsonObject
        {
            ["active"] = true, ["url"] = "http://192.168.1.50:47800", ["secondsLeft"] = 812, ["clientAvailable"] = true,
            ["request"] = new JsonObject { ["pin"] = "4826", ["name"] = "ROG Ally", ["address"] = "192.168.1.73" },
            ["lastPaired"] = "", ["note"] = "",
        }
        : new JsonObject
        {
            ["active"] = false, ["url"] = null, ["secondsLeft"] = 0, ["clientAvailable"] = true,
            ["request"] = null, ["lastPaired"] = "", ["note"] = "",
        };

    static JsonObject State()
    {
        var setupScene = Scene == "welcome" || Scene.StartsWith("setup-");
        var doneSteps = Scene.StartsWith("setup-") && int.TryParse(Scene[6..], out var n) ? n : Scene == "welcome" ? -1 : 7;

        var steps = new JsonArray();
        for (var i = 0; i < SetupSteps.Length; i++)
        {
            var s = SetupSteps[i];
            var state = doneSteps < 0 ? "pending" : i < doneSteps ? "done" : i == doneSteps ? "running" : "pending";
            steps.Add(new JsonObject
            {
                ["id"] = s.Id, ["label"] = s.Label, ["state"] = state,
                ["detail"] = state == "done" ? s.Detail : state == "running" ? RunningText[i].Running : "",
            });
        }
        var running = setupScene && doneSteps >= 0 && doneSteps < SetupSteps.Length;

        var streaming = Scene == "streaming";
        var events = new JsonArray(
            new JsonObject { ["time"] = "2026-11-23 20:31:41", ["type"] = "connected", ["app"] = "Steam Big Picture" },
            new JsonObject { ["time"] = "2026-11-23 18:02:10", ["type"] = "disconnected" },
            new JsonObject { ["time"] = "2026-11-23 16:45:03", ["type"] = "connected", ["app"] = "Desktop" });
        if (!streaming) events.Insert(0, new JsonObject { ["time"] = "2026-11-23 22:10:55", ["type"] = "disconnected" });

        JsonObject Check(string id, string title, string state, string detail, string? action = null, string? label = null) => new()
        {
            ["id"] = id, ["title"] = title, ["state"] = state, ["detail"] = detail, ["action"] = action, ["actionLabel"] = label,
        };

        return new JsonObject
        {
            ["version"] = AppInfo.Version,
            ["host"] = new JsonObject
            {
                ["name"] = "GAMING-PC", ["ip"] = "192.168.1.50", ["gpu"] = "NVIDIA GeForce RTX 4070", ["vendor"] = "nvidia",
                ["display"] = new JsonObject { ["w"] = 2560, ["h"] = 1440, ["hz"] = 165 },
                ["wired"] = true, ["category"] = "Private", ["vigem"] = true, ["firewall"] = true, ["wol"] = true,
                ["nic"] = new JsonObject { ["speed"] = "1 Gbps" },
            },
            ["sunshine"] = new JsonObject
            {
                ["installed"] = !setupScene, ["version"] = "2026.914.233613", ["service"] = setupScene ? "Missing" : "Running",
                ["api"] = setupScene ? "Down" : "Ok", ["webUi"] = "https://localhost:47990", ["virtualHid"] = false,
            },
            ["activity"] = new JsonObject
            {
                ["streaming"] = streaming, ["since"] = streaming ? "2026-11-23 20:31:41" : null,
                ["app"] = streaming ? "Steam Big Picture" : null,
                ["encoders"] = new JsonArray("H.264", "HEVC", "AV1"), ["gamepadMissing"] = false, ["events"] = events,
            },
            ["tuning"] = Tuning(),
            ["setupDone"] = !setupScene,
            ["ready"] = !setupScene,
            ["setup"] = new JsonObject { ["running"] = running, ["problem"] = null, ["done"] = !setupScene, ["steps"] = steps },
            ["checks"] = new JsonArray(
                Check("engine", "Streaming engine", "ok", "Sunshine 2026.914.233613 is running and starts with Windows."),
                Check("login", "Private login", "ok", "Solstice holds the Sunshine login, encrypted for your Windows account."),
                Check("encoder", "Video encoder", "ok", "H.264, HEVC, AV1 hardware encoding on NVIDIA GeForce RTX 4070."),
                Check("controller", "Controller driver", "ok", "ViGEmBus is installed: the handheld's buttons and sticks arrive as an Xbox controller."),
                Check("network", "Network", "ok", "Wired at 1 Gbps. This PC's address is 192.168.1.50."),
                Check("firewall", "Firewall", "ok", "Windows Firewall lets devices reach Sunshine."),
                Check("pads", "Other controllers", "ok", "No controller is connected to this PC, so games will follow the handheld's."),
                Check("wake", "Wake from sleep", "ok", "Wake-on-LAN is on: the handheld can wake this PC when it is asleep.")),
            ["link"] = Link(),
        };
    }
}
