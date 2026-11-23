using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Solstice;

/// <summary>One thing that can be offered to a streaming device: a launcher or an installed game.</summary>
record LibItem(string Id, string Source, string Kind, string Name, string Cmd, string? UndoCmd = null, string? ArtDir = null);

/// <summary>
/// Finds launchers and installed games on this PC and keeps Sunshine's app list in step with the
/// user's selection, so they appear as tiles (with cover art) in Moonlight on the handheld.
/// </summary>
static class Library
{
    static readonly Regex VdfPair = new("\"(\\w+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

    // Steam entries that are not games.
    static readonly Regex SteamNoise = new(@"^(Steamworks Common Redistributables|Steam Linux Runtime.*|Proton.*|Steam Controller Configs.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<LibItem> Detect()
    {
        var items = new List<LibItem>();
        Try(() => Launchers(items));
        Try(() => SteamGames(items));
        Try(() => EpicGames(items));
        return items;
    }

    static void Try(Action a)
    {
        try { a(); } catch { /* a broken launcher install must not hide the others */ }
    }

    static string? Reg(RegistryKey hive, string path, string value)
    {
        try
        {
            using var k = hive.OpenSubKey(path);
            return k?.GetValue(value) as string;
        }
        catch { return null; }
    }

    static string Quote(string exe) => "\"" + exe + "\"";

    static void Launchers(List<LibItem> items)
    {
        if (SteamRoot() != null)
            items.Add(new LibItem("launcher:steam", "Steam", "launcher", "Steam Big Picture", "steam://open/bigpicture", "steam://close/bigpicture"));

        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var epic = Path.Combine(Environment.GetFolderPath(pf), @"Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe");
            if (File.Exists(epic))
            {
                items.Add(new LibItem("launcher:epic", "Epic Games", "launcher", "Epic Games Launcher", Quote(epic)));
                break;
            }
        }

        var ea = Reg(Registry.LocalMachine, @"SOFTWARE\Electronic Arts\EA Desktop", "LauncherAppPath")
                 ?? Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Electronic Arts\EA Desktop", "LauncherAppPath");
        if (ea != null && File.Exists(ea))
            items.Add(new LibItem("launcher:ea", "EA", "launcher", "EA app", Quote(ea)));

        var ubiDir = Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Ubisoft\Launcher", "InstallDir");
        if (ubiDir != null)
        {
            var ubi = new[] { "UbisoftConnect.exe", "upc.exe" }.Select(f => Path.Combine(ubiDir, f)).FirstOrDefault(File.Exists);
            if (ubi != null) items.Add(new LibItem("launcher:ubisoft", "Ubisoft", "launcher", "Ubisoft Connect", Quote(ubi)));
        }

        try
        {
            using var pk = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (pk != null && pk.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.GamingApp_", StringComparison.OrdinalIgnoreCase)))
                items.Add(new LibItem("launcher:xbox", "Xbox", "launcher", "Xbox app",
                    @"explorer.exe shell:AppsFolder\Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App"));
        }
        catch { /* no Xbox app */ }

        var gog = Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\GOG.com\GalaxyClient\paths", "client");
        if (gog != null && File.Exists(Path.Combine(gog, "GalaxyClient.exe")))
            items.Add(new LibItem("launcher:gog", "GOG", "launcher", "GOG Galaxy", Quote(Path.Combine(gog, "GalaxyClient.exe"))));

        var bnet = Reg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Battle.net", "InstallLocation");
        if (bnet != null && File.Exists(Path.Combine(bnet, "Battle.net Launcher.exe")))
            items.Add(new LibItem("launcher:battlenet", "Battle.net", "launcher", "Battle.net", Quote(Path.Combine(bnet, "Battle.net Launcher.exe"))));

        var playnite = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Playnite\Playnite.FullscreenApp.exe");
        if (File.Exists(playnite))
            items.Add(new LibItem("launcher:playnite", "Playnite", "launcher", "Playnite Fullscreen", Quote(playnite)));
    }

    static string? SteamRoot()
    {
        var p = Reg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (p == null) return null;
        p = Path.GetFullPath(p.Replace('/', '\\'));
        return Directory.Exists(p) ? p : null;
    }

    static void SteamGames(List<LibItem> items)
    {
        var root = SteamRoot();
        if (root == null) return;

        var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root };
        var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in VdfPair.Matches(File.ReadAllText(vdf)))
                if (m.Groups[1].Value == "path")
                    libs.Add(m.Groups[2].Value.Replace(@"\\", @"\"));

        var seen = new HashSet<string>();
        foreach (var lib in libs)
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string? id = null, name = null;
                foreach (Match m in VdfPair.Matches(File.ReadAllText(acf)))
                {
                    if (m.Groups[1].Value == "appid") id ??= m.Groups[2].Value;
                    else if (m.Groups[1].Value == "name") name ??= m.Groups[2].Value.Replace("\\\"", "\"");
                }
                if (id == null || name == null || SteamNoise.IsMatch(name) || !seen.Add(id)) continue;
                items.Add(new LibItem($"steam:{id}", "Steam", "game", name, $"steam://rungameid/{id}",
                    ArtDir: Path.Combine(root, @"appcache\librarycache", id)));
            }
        }
    }

    static void EpicGames(List<LibItem> items)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                if (o["bIsApplication"]?.GetValue<bool>() == false) continue; // DLC and add-ons
                var cats = o["AppCategories"] as JsonArray;
                if (cats != null && !cats.Any(c => c?.ToString() == "games")) continue;
                string? name = o["DisplayName"]?.ToString(), app = o["AppName"]?.ToString(),
                    ns = o["CatalogNamespace"]?.ToString(), item = o["CatalogItemId"]?.ToString();
                if (name == null || app == null) continue;
                var target = ns != null && item != null ? $"{ns}%3A{item}%3A{app}" : app;
                items.Add(new LibItem($"epic:{app}", "Epic Games", "game", name,
                    $"com.epicgames.launcher://apps/{target}?action=launch&silent=true"));
            }
            catch { /* skip unreadable manifest */ }
        }
    }

    // ---- Sunshine app list ----------------------------------------------------------------

    static JsonObject ToApp(LibItem item)
    {
        var app = new JsonObject
        {
            ["name"] = item.Name,
            ["cmd"] = item.Cmd,
            ["image-path"] = CoverFor(item),
            ["auto-detach"] = true,
            ["wait-all"] = true,
            ["exit-timeout"] = 5,
            ["index"] = -1,
        };
        if (item.UndoCmd != null)
            app["prep-cmd"] = new JsonArray(new JsonObject { ["do"] = "", ["undo"] = item.UndoCmd });
        return app;
    }

    public static async Task<JsonObject> Snapshot()
    {
        var detected = Detect();
        var apps = await Sunshine.GetApps();
        var names = apps.OfType<JsonObject>().Select(a => a["name"]?.ToString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = detected.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var list = new JsonArray();
        foreach (var d in detected)
            list.Add(new JsonObject
            {
                ["id"] = d.Id, ["source"] = d.Source, ["kind"] = d.Kind, ["name"] = d.Name,
                ["added"] = names.Contains(d.Name),
            });

        // Entries that exist in Sunshine but were not found by detection (Desktop, hand-made apps).
        var other = new JsonArray();
        foreach (var a in apps.OfType<JsonObject>())
        {
            var n = a["name"]?.ToString() ?? "";
            if (!known.Contains(n)) other.Add(n);
        }
        return new JsonObject { ["items"] = list, ["other"] = other };
    }

    /// <summary>Makes Sunshine's list match the selection for everything detection knows about.</summary>
    public static async Task<JsonObject> Sync(HashSet<string> selected)
    {
        var detected = Detect();
        int added = 0, removed = 0;

        foreach (var d in detected.Where(d => !selected.Contains(d.Id)))
        {
            var apps = await Sunshine.GetApps(); // re-read: Sunshine re-sorts after every change
            var index = IndexOf(apps, d.Name);
            if (index < 0) continue;
            await Sunshine.DeleteApp(index);
            removed++;
        }

        var current = await Sunshine.GetApps();
        foreach (var d in detected.Where(d => selected.Contains(d.Id)))
        {
            if (IndexOf(current, d.Name) >= 0) continue;
            await Sunshine.SaveApp(ToApp(d));
            added++;
        }

        // Repair published entries whose box art path is missing or out of date (for example
        // after the cover folder moved), so Sunshine never falls back to its blank box.
        foreach (var d in detected.Where(d => selected.Contains(d.Id)))
        {
            var apps = await Sunshine.GetApps();
            var index = IndexOf(apps, d.Name);
            if (index < 0 || apps[index] is not JsonObject app) continue;
            var cover = CoverFor(d);
            var image = app["image-path"]?.ToString() ?? "";
            if (cover.Length == 0 || string.Equals(image, cover, StringComparison.OrdinalIgnoreCase)) continue;
            if (d.Id == "launcher:steam" && image.Length > 0 && !Path.IsPathRooted(image)) continue; // Sunshine's own Steam art
            var fixedApp = (JsonObject)app.DeepClone();
            fixedApp["image-path"] = cover;
            fixedApp["index"] = index;
            await Sunshine.SaveApp(fixedApp);
        }
        return new JsonObject { ["added"] = added, ["removed"] = removed };
    }

    static int IndexOf(JsonArray apps, string name)
    {
        for (var i = 0; i < apps.Count; i++)
            if (string.Equals(apps[i]?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // ---- Cover art ------------------------------------------------------------------------

    static string CoverPath(string id) => Path.Combine(Paths.Covers, Regex.Replace(id, @"[^A-Za-z0-9_-]", "_") + ".png");

    public static string? CoverFor(string id)
    {
        var path = CoverPath(id);
        if (File.Exists(path)) return path;
        var item = Detect().FirstOrDefault(d => d.Id == id);
        return item == null ? null : CoverFor(item);
    }

    /// <summary>Returns a 600x900 PNG for the item, building it once. Sunshine only accepts PNG box art.</summary>
    public static string CoverFor(LibItem item) => Render(item, CoverPath(item.Id));

    /// <summary>Draws the box art for an item into the given file, unless it already exists.</summary>
    public static string Render(LibItem item, string path)
    {
        if (File.Exists(path)) return path;
        try
        {
            using var bmp = new Bitmap(600, 900, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (!DrawStoreArt(g, item)) DrawGenerated(g, item, FindArt(item, wide: true));
            bmp.Save(path, ImageFormat.Png);
        }
        catch
        {
            // Art is cosmetic: without it Sunshine falls back to its default box image.
            return "";
        }
        return path;
    }

    /// <summary>Picks cached store art from the launcher's own files: portrait capsule, or the wide header.</summary>
    static string? FindArt(LibItem item, bool wide)
    {
        if (item.ArtDir == null || !Directory.Exists(item.ArtDir)) return null;
        string? best = null;
        double bestScore = double.MaxValue;
        foreach (var f in Directory.EnumerateFiles(item.ArtDir, "*.jpg", SearchOption.AllDirectories))
        {
            try
            {
                using var img = Image.FromFile(f);
                if (img.Width < 200) continue;
                var ratio = (double)img.Height / img.Width;
                // Portrait capsules are 2:3 (ratio 1.5); headers are roughly 460x215 (ratio 0.47).
                var score = Math.Abs(ratio - (wide ? 0.47 : 1.5));
                if (score < 0.12 && score < bestScore) { best = f; bestScore = score; }
            }
            catch { /* unreadable image */ }
        }
        return best;
    }

    static bool DrawStoreArt(Graphics g, LibItem item)
    {
        var art = FindArt(item, wide: false);
        if (art == null) return false;
        using var img = Image.FromFile(art);
        g.DrawImage(img, new Rectangle(0, 0, 600, 900));
        return true;
    }

    static (Color A, Color B) Palette(LibItem item) => item.Source switch
    {
        "Steam" => (Color.FromArgb(23, 42, 71), Color.FromArgb(9, 14, 26)),
        "Epic Games" => (Color.FromArgb(52, 52, 58), Color.FromArgb(12, 12, 14)),
        "EA" => (Color.FromArgb(112, 34, 42), Color.FromArgb(22, 10, 14)),
        "Ubisoft" => (Color.FromArgb(28, 62, 122), Color.FromArgb(8, 14, 32)),
        "Xbox" => (Color.FromArgb(18, 92, 44), Color.FromArgb(6, 22, 12)),
        "GOG" => (Color.FromArgb(84, 40, 116), Color.FromArgb(16, 8, 26)),
        "Battle.net" => (Color.FromArgb(18, 72, 128), Color.FromArgb(6, 14, 30)),
        _ => (Color.FromArgb(96, 62, 22), Color.FromArgb(18, 12, 8)),
    };

    static void DrawGenerated(Graphics g, LibItem item, string? header)
    {
        var (a, b) = Palette(item);
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, 600, 900), a, b, 60f))
            g.FillRectangle(bg, 0, 0, 600, 900);

        var textTop = 330;
        if (header != null)
        {
            try
            {
                using var img = Image.FromFile(header);
                var h = (int)(600.0 * img.Height / img.Width);
                g.DrawImage(img, new Rectangle(0, 150, 600, h));
                textTop = 150 + h + 50;
            }
            catch { /* draw text only */ }
        }

        using var titleFont = new Font("Segoe UI Semibold", item.Name.Length > 22 ? 34 : 44, FontStyle.Regular, GraphicsUnit.Pixel);
        using var tagFont = new Font("Segoe UI", 24, FontStyle.Regular, GraphicsUnit.Pixel);
        using var white = new SolidBrush(Color.FromArgb(245, 245, 245));
        using var dim = new SolidBrush(Color.FromArgb(170, 255, 255, 255));
        var fmt = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisWord };
        g.DrawString(item.Name, titleFont, white, new RectangleF(48, textTop, 504, 260), fmt);
        g.DrawString(item.Kind == "launcher" ? "Launcher" : item.Source, tagFont, dim, new RectangleF(50, 812, 500, 40), fmt);
    }
}
