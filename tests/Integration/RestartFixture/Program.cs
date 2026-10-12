using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2) return 1;
        string status = args[0];
        var application = new Application();
        var window = new Window { Width = 100, Height = 100, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = true };
        window.Loaded += (s,e) => { using (var process = Process.GetCurrentProcess()) File.WriteAllText(status, process.Id.ToString()); };
        var elapsed = Stopwatch.StartNew();
        window.Closing += (s,e) => {
            File.WriteAllText(status + ".close-requested", "normal close received");
            if (args.Length == 2 && args[1] == "ignore-close" &&
                !File.Exists(status + ".stop") && elapsed.Elapsed < TimeSpan.FromSeconds(30))
            {
                e.Cancel = true;
            }
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (s,e) => {
            if (File.Exists(status + ".stop") || elapsed.Elapsed > TimeSpan.FromSeconds(30)) window.Close();
        };
        timer.Start();
        return application.Run(window);
    }
}
