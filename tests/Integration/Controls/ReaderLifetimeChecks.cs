using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using PlayniteUpdateStatus.Launchers.Steam;

internal static partial class Program
{
    private static void ReaderLifetimeTests()
    {
        Test("reader initialization failure stops its already-started helper", () => ReaderLifetime(true));
        Test("reader Dispose stops its helper, rejects late frames and never restarts", () => ReaderLifetime(false));
    }

    // Isolated child with synthetic frames, a parent check and a bounded lifetime.
    // No Steam connection or Steam commands are used by either test.
    private static int ReaderLifetimeChild(int parentId)
    {
        using (var parent = Process.GetProcessById(parentId))
        {
            var sample = new SteamLiveSample { AppId = 550, InstallState = 270, UpdateState = 97,
                HasCounters = true, Downloaded = 50, DownloadTotal = 100, Staged = 70, StageTotal = 100,
                UiWorkPercent = 70, DownloadsEnabled = true };
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!parent.HasExited && DateTime.UtcNow < deadline)
            {
                Console.WriteLine(sample.Frame());
                Console.Out.Flush();
                Thread.Sleep(100);
            }
        }
        return 0;
    }

    private static void ReaderLifetime(bool initializationFails)
    {
        string root = Path.Combine(Path.GetTempPath(), "ReaderLifetime-" + Guid.NewGuid());
        Process created = null, owned = null;
        int starts = 0;
        Func<string, string, Process> factory = (helper, arguments) => {
            starts++;
            created = new Process {
                StartInfo = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tests.exe"),
                    "--reader-lifetime-child " + Process.GetCurrentProcess().Id) {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            return created;
        };
        Action<string, bool> log = (message, warning) => {
            if (!message.StartsWith("Started Steam UI reader", StringComparison.Ordinal)) return;
            owned = Process.GetProcessById(created.Id);
            // Pin the handle so cleanup can only target this exact owned child,
            // even if its PID is reused after it exits.
            var handle = owned.Handle;
            if (initializationFails) throw new IOException("Injected post-start initialization failure");
        };
        var constructor = typeof(SteamLiveReader).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(string), typeof(uint), typeof(Action<string, bool>), typeof(Func<string, string, Process>) }, null);
        var reader = (SteamLiveReader)constructor.Invoke(new object[] { root, (uint)550, log, factory });
        try
        {
            reader.TryRead();
            Eq(true, owned != null);
            if (initializationFails) Eq(true, owned.WaitForExit(3000));
            else
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (reader.TryRead() == null && DateTime.UtcNow < deadline) Thread.Sleep(20);
                Eq(true, reader.TryRead() != null);
                Eq(false, owned.HasExited);
                reader.Dispose();
                Eq(true, owned.WaitForExit(3000));
            }
            reader.Dispose();
            Eq((SteamLiveSample)null, reader.TryRead());
            Thread.Sleep(150);
            Eq((SteamLiveSample)null, reader.TryRead());
            Eq(1, starts);
        }
        finally
        {
            reader.Dispose();
            if (owned != null)
            {
                if (!owned.HasExited) { owned.Kill(); owned.WaitForExit(3000); }
                owned.Dispose();
            }
        }
    }
}
