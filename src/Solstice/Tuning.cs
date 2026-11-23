using System.Text.Json.Nodes;

namespace Solstice;

/// <summary>
/// Turns a few plain-language choices into Sunshine configuration keys. Only the keys listed
/// here are ever touched; anything else the user set in Sunshine's own UI is preserved.
/// </summary>
static class Tuning
{
    public static readonly string[] Profiles = { "latency", "balanced", "picture" };

    public static JsonObject Current()
    {
        var s = Settings.Load();
        return new JsonObject
        {
            ["profile"] = s.Str("profile", "balanced"),
            ["matchDisplay"] = s.Bool("matchDisplay", true),
            ["lockWebUi"] = s.Bool("lockWebUi", true),
            ["guideHold"] = s.Bool("guideHold", true),
        };
    }

    /// <summary>Config keys for the given choices. A null value means "remove, use Sunshine's default".</summary>
    public static Dictionary<string, string?> Keys(JsonObject t)
    {
        var profile = t.Str("profile", "balanced");
        var k = new Dictionary<string, string?>();

        // Encoder trade-off. Every vendor's keys are written; Sunshine ignores the ones for
        // hardware that is not present, so the same profile works if the GPU is swapped later.
        switch (profile)
        {
            case "latency":
                k["nvenc_preset"] = "1";
                k["nvenc_twopass"] = "disabled";
                k["nvenc_spatial_aq"] = null;
                k["amd_usage"] = "ultralowlatency";
                k["qsv_preset"] = "veryfast";
                break;
            case "picture":
                k["nvenc_preset"] = "4";
                k["nvenc_twopass"] = "full_res";
                k["nvenc_spatial_aq"] = "enabled";
                k["amd_usage"] = "lowlatency_high_quality";
                k["qsv_preset"] = "slow";
                break;
            default:
                k["nvenc_preset"] = null;
                k["nvenc_twopass"] = null;
                k["nvenc_spatial_aq"] = null;
                k["amd_usage"] = null;
                k["qsv_preset"] = null;
                break;
        }

        // Switch the monitor to the handheld's resolution and refresh rate for the session, then
        // switch back. The game then renders at the device's native mode instead of being scaled.
        var match = t.Bool("matchDisplay", true);
        k["dd_configuration_option"] = match ? "ensure_active" : null;
        k["dd_resolution_option"] = match ? "auto" : null;
        k["dd_refresh_rate_option"] = match ? "auto" : null;

        // Sunshine's admin page is reachable from the whole LAN by default.
        k["origin_web_ui_allowed"] = t.Bool("lockWebUi", true) ? "pc" : null;

        // Hold Back/View for 1.5 s to send the Guide button (opens Steam's overlay on a handheld).
        k["back_button_timeout"] = t.Bool("guideHold", true) ? "1500" : null;
        return k;
    }

    /// <summary>
    /// Saves the choices and pushes them to Sunshine. Returns "unchanged", "applied" (Sunshine was
    /// restarted and is live with the new settings) or "pending" (saved, but a device is streaming
    /// so the restart was skipped and the settings start with the next restart).
    /// </summary>
    public static async Task<string> Apply(JsonObject choices, bool allowRestart = true)
    {
        var profile = choices.Str("profile", "balanced");
        if (Array.IndexOf(Profiles, profile) < 0) throw new AppException("Unknown profile.");

        Settings.Update(s =>
        {
            s["profile"] = profile;
            foreach (var key in new[] { "matchDisplay", "lockWebUi", "guideHold" })
                s[key] = choices.Bool(key, true);
        });

        var changed = await Sunshine.PatchConfig(Keys(Current()));
        if (!changed) return "unchanged";
        if (!allowRestart || Probe.Activity()["streaming"]!.GetValue<bool>()) return "pending";

        await Sunshine.Restart();
        await Task.Delay(1500);
        await Sunshine.WaitUntilUp(40);
        return "applied";
    }
}
