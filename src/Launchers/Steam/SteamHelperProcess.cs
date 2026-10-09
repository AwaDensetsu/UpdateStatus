using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Shared process setup/cleanup only. Protocols, command permissions and
    // freshness remain owned by the individual readers and command client.
    internal static class SteamHelperProcess
    {
        public static string ExecutablePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "SteamUpdateReader.exe"); }
        }

        public static Process Create(string helper, string arguments)
        {
            return new Process {
                StartInfo = new ProcessStartInfo(helper, arguments) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(helper)
                }
            };
        }

        // Callers pass only their own helper process, never a launcher/game.
        public static void Stop(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
    }
}
