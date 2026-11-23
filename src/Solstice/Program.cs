using System.Drawing;

namespace Solstice;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppInfo.Name = "Solstice";
        AppInfo.Title = "Solstice";

        var demo = Array.IndexOf(args, "--demo");
        if (demo >= 0)
        {
            // Simulated data for screenshots: nothing real is read or changed.
            if (demo + 1 < args.Length && !args[demo + 1].StartsWith("--")) Demo.Scene = args[demo + 1];
            return Shell.Run(args, new ShellOptions(new Size(1180, 780), new Size(940, 640)), Demo.Handle, Demo.File);
        }

        return Shell.Run(args,
            new ShellOptions(new Size(1180, 780), new Size(940, 640)),
            HostApi.Handle, HostApi.File,
            onExit: DeviceLink.Stop);
    }
}
