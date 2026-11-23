using System.Text.Json.Nodes;

namespace Solstice;

/// <summary>
/// Turns a few plain-language choices into Sunshine configuration keys. Only the keys listed
/// here are ever touched; anything else the user set in Sunshine's own UI is preserved.
/// </summary>
static class Tuning
{
    public static readonly string[] Profiles = { "latency", "balanced", "picture" };

    /// <summary>Emulated controller types Solstice offers (Sunshine's <c>gamepad</c> setting).</summary>
    public static readonly string[] Controllers = { "auto", "ds4", "xseries" };

    static readonly string[] Toggles = { "matchDisplay", "lockWebUi", "guideHold", "hideTray" };

    public static JsonObject Current()
    {
        var s = Settings.Load();
        return new JsonObject
        {
            ["profile"] = s.Str("profile", "balanced"),
            ["controller"] = s.Str("controller", "auto"),
            ["matchDisplay"] = s.Bool("matchDisplay", true),
            ["lockWebUi"] = s.Bool("lockWebUi", true),
            ["guideHold"] = s.Bool("guideHold", true),
            ["hideTray"] = s.Bool("hideTray", true),
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

        // Sunshine's tray icon has a Quit item that stops the streaming service outright, and it is
        // easy to click by mistake. Solstice covers everything the icon offered.
        k["system_tray"] = t.Bool("hideTray", true) ? "disabled" : null;

        // Which controller the PC creates for the handheld. Xbox 360 (Sunshine's automatic choice)
        // works with nearly every game. Some Xbox app games read controllers through GameInput,
        // which mishandles Xbox 360-type pads; a DualShock 4 or, with LizardByte's Virtual HID
        // Driver, an Xbox Series controller avoids that.
        var controller = t.Str("controller", "auto");
        k["gamepad"] = controller == "auto" ? null : controller;
        k["motion_as_ds4"] = controller == "ds4" ? "enabled" : null;
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
        var controller = choices.Str("controller", "auto");
        if (Array.IndexOf(Controllers, controller) < 0) throw new AppException("Unknown controller type.");
        if (controller == "xseries" && !(await Sunshine.VirtualHid()).Installed)
            throw new AppException("Xbox Series controllers need LizardByte's Virtual HID Driver, which is not installed on this PC. Choose Automatic or PlayStation instead.");

        Settings.Update(s =>
        {
            s["profile"] = profile;
            s["controller"] = controller;
            foreach (var key in Toggles)
                s[key] = choices.Bool(key, true);
        });

        return await Push(allowRestart);
    }

    /// <summary>Writes the saved choices to Sunshine, restarting it only when something changed.</summary>
    static async Task<string> Push(bool allowRestart)
    {
        var changed = await Sunshine.PatchConfig(Keys(Current()));
        if (!changed) return "unchanged";
        if (!allowRestart || Probe.Activity()["streaming"]!.GetValue<bool>()) return "pending";

        await Sunshine.Restart();
        await Task.Delay(1500);
        await Sunshine.WaitUntilUp(40);
        return "applied";
    }

    static int _ensured;

    /// <summary>
    /// Once per launch, brings Sunshine's settings in line with the saved choices, so settings
    /// added in a newer Solstice (such as hiding the tray icon) reach existing installs.
    /// </summary>
    public static async Task EnsureOnce()
    {
        if (Interlocked.Exchange(ref _ensured, 1) == 1) return;
        try { await Push(allowRestart: true); }
        catch { _ensured = 0; }
    }
}
