using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Solstice;

/// <summary>Options for the app window.</summary>
sealed record ShellOptions(Size Size, Size MinSize, bool StartMaximized = false);

/// <summary>
/// Shared entry point: one native window hosting the UI in the system's WebView2 runtime,
/// backed by a loopback <see cref="WebHost"/>. Also provides a headless mode used for testing:
///   --serve --port N --token T [--webroot DIR]
/// </summary>
static class Shell
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static readonly Color Surface = Color.FromArgb(13, 15, 20);

    public static int Run(string[] args, ShellOptions options, WebHost.ApiHandler api, Func<string, string?>? files = null, Action? onExit = null)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (args.Contains("--serve"))
        {
            var host = new WebHost(api, files, int.TryParse(Arg("--port"), out var p) ? p : 0, Arg("--token"), Arg("--webroot"));
            host.Start();
            Console.WriteLine(host.StartUrl);
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        using var mutex = new Mutex(true, $"Local\\{AppInfo.Name}.SingleInstance", out var first);
        if (!first)
        {
            var existing = FindWindow(null, AppInfo.Title);
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, 9); // SW_RESTORE
                SetForegroundWindow(existing);
            }
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var server = new WebHost(api, files);
        server.Start();
        try
        {
            Application.Run(new MainForm(server, options));
        }
        finally
        {
            server.Stop();
            onExit?.Invoke();
        }
        return 0;
    }

    sealed class MainForm : Form
    {
        readonly WebView2 _view = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Surface };
        readonly WebHost _server;
        FormWindowState _beforeFullscreen;
        bool _fullscreen;

        public MainForm(WebHost server, ShellOptions options)
        {
            _server = server;
            Text = AppInfo.Title;
            BackColor = Surface;
            ClientSize = options.Size;
            MinimumSize = options.MinSize;
            StartPosition = FormStartPosition.CenterScreen;
            if (options.StartMaximized) WindowState = FormWindowState.Maximized;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                if (s != null) Icon = new Icon(s);
            Controls.Add(_view);
            Load += async (_, _) => await Init();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Dark title bar that matches the app surface (Windows 11; ignored on older builds).
            var on = 1;
            DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int));
            var caption = Surface.R | (Surface.G << 8) | (Surface.B << 16);
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        }

        async Task Init()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Paths.WebView);
                await _view.EnsureCoreWebView2Async(env);
            }
            catch (Exception)
            {
                MessageBox.Show(this,
                    $"{AppInfo.Title} needs the Microsoft Edge WebView2 Runtime, which normally ships with Windows 11.\n\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2 and open {AppInfo.Title} again.",
                    AppInfo.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
                return;
            }

            var core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            // The window only ever shows the app's own page; anything else opens in the real browser.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Proc.OpenExternal(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith($"http://127.0.0.1:{_server.Port}/"))
                {
                    e.Cancel = true;
                    Proc.OpenExternal(e.Uri);
                }
            };
            core.WebMessageReceived += (_, e) =>
            {
                var msg = e.TryGetWebMessageAsString();
                if (msg == "fullscreen") ToggleFullscreen();
                else if (msg == "close") Close();
            };
            core.Navigate(_server.StartUrl);
        }

        void ToggleFullscreen()
        {
            if (!_fullscreen)
            {
                _beforeFullscreen = WindowState;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = _beforeFullscreen;
            }
            _fullscreen = !_fullscreen;
        }
    }
}
