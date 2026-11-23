using System.Diagnostics;
using Microsoft.Win32;

namespace Solstice;

/// <summary>Drives the Moonlight client through its command line and seeds its saved settings.</summary>
static class Moonlight
{
    public const string WingetId = "MoonlightGameStreamingProject.Moonlight";

    public static string? Exe()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("SOLSTICE_MOONLIGHT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Moonlight Game Streaming\Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Moonlight Game Streaming\Moonlight.exe"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    public static string? Version()
    {
        var exe = Exe();
        if (exe == null) return null;
        try { return FileVersionInfo.GetVersionInfo(exe).ProductVersion; } catch { return null; }
    }

    static string Dir(string exe) => Path.GetDirectoryName(exe)!;

    /// <summary>Runs a Moonlight command and waits for it. The working directory is Moonlight's own folder.</summary>
    public static Task<ProcResult> Run(IEnumerable<string> args, int timeoutMs)
    {
        var exe = Exe() ?? throw new AppException("Moonlight is not installed.");
        return Proc.Run(exe, args, timeoutMs, Dir(exe));
    }

    /// <summary>Starts a Moonlight command without waiting (pairing window, stream).</summary>
    public static Process Start(IEnumerable<string> args)
    {
        var exe = Exe() ?? throw new AppException("Moonlight is not installed.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Dir(exe) };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new AppException("Moonlight could not be started.");
    }

    /// <summary>True when this device is paired with the host: listing its apps only works after pairing.</summary>
    public static async Task<bool> IsPaired(string host)
    {
        var r = await Run(new[] { "list", host }, 20000);
        return r.Code == 0;
    }

    /// <summary>App names published by the host, in the host's order.</summary>
    public static async Task<List<string>> Apps(string host)
    {
        var r = await Run(new[] { "list", host }, 20000);
        if (r.Code != 0) return new List<string>();
        return r.Out.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    /// <summary>
    /// Writes Moonlight's own saved settings so that streams started from Moonlight's window use
    /// the same choices as streams started from this app. Booleans are stored the way Qt stores
    /// them ("true"/"false" strings); numbers as DWORDs.
    /// </summary>
    public static void SeedSettings(StreamSettings s)
    {
        if (Environment.GetEnvironmentVariable("SOLSTICE_MOONLIGHT") != null) return; // portable test build keeps its own ini
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Moonlight Game Streaming Project\Moonlight");
            void Int(string n, int v) => key.SetValue(n, v, RegistryValueKind.DWord);
            void Bool(string n, bool v) => key.SetValue(n, v ? "true" : "false", RegistryValueKind.String);
            Int("width", s.Width);
            Int("height", s.Height);
            Int("fps", s.Fps);
            Int("bitrate", s.BitrateKbps);
            Bool("autoadjustbitrate", false);
            Int("videocfg", s.Codec switch { "H.264" => 1, "AV1" => 4, _ => 2 });
            Int("windowmode", 0);
            Bool("vsync", s.VSync);
            Bool("framepacing", s.VSync);
            Bool("gameopts", true);
            Bool("quitAppAfter", s.QuitAfter);
            Bool("hostaudio", false);
            Bool("hdr", s.Hdr);
            Bool("showperfoverlay", s.Overlay);
            Int("capturesyskeys", 1);
        }
        catch { /* Moonlight's own defaults still work */ }
    }

    public static List<string> StreamArgs(string host, string app, StreamSettings s)
    {
        var a = new List<string>
        {
            "stream", host, app,
            "--resolution", $"{s.Width}x{s.Height}",
            "--fps", s.Fps.ToString(),
            "--bitrate", s.BitrateKbps.ToString(),
            "--video-codec", s.Codec,
            "--display-mode", "fullscreen",
            "--capture-system-keys", "fullscreen",
            "--game-optimization",
            "--no-audio-on-host",
            "--keep-awake",
        };
        a.Add(s.VSync ? "--vsync" : "--no-vsync");
        a.Add(s.VSync ? "--frame-pacing" : "--no-frame-pacing");
        a.Add(s.QuitAfter ? "--quit-after" : "--no-quit-after");
        a.Add(s.Hdr ? "--hdr" : "--no-hdr");
        a.Add(s.Overlay ? "--performance-overlay" : "--no-performance-overlay");
        return a;
    }
}

/// <summary>What a stream is launched with. Derived from the screen, then adjustable by the user.</summary>
sealed record StreamSettings(int Width, int Height, int Fps, int BitrateKbps, string Codec, bool VSync, bool Hdr, bool Overlay, bool QuitAfter);
