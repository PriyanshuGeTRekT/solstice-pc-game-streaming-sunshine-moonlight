using System.Drawing;

namespace Solstice;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppInfo.Name = "SolsticePlay";
        AppInfo.Title = "Solstice Play";

        var options = new ShellOptions(new Size(1100, 700), new Size(760, 520), StartMaximized: true);
        var demo = Array.IndexOf(args, "--demo");
        if (demo >= 0)
        {
            // Simulated data for screenshots: nothing real is read or changed.
            if (demo + 1 < args.Length && !args[demo + 1].StartsWith("--")) Demo.Scene = args[demo + 1];
            return Shell.Run(args, options, Demo.Handle);
        }
        return Shell.Run(args, options, PlayApi.Handle);
    }
}
