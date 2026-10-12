using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using PlayniteUpdateStatus.Launchers.Steam;

internal static class Program
{
    public static int Main(string[] args)
    {
        if ((args.Length == 2 || args.Length == 3) && (args[1] == "--enable-local-access" || args[1] == "--disable-local-access"))
        {
            try
            {
                if (args.Length == 3 && args[2] != System.Security.Principal.WindowsIdentity.GetCurrent().User.Value) return 1;
                var setup = new SteamConnectionSetup(args[0]);
                if (args[1] == "--enable-local-access") setup.Enable(); else setup.Remove();
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("Local access setup failed: " + error.Message); return 1; }
        }
        if (args.Length < 2) return 2;
        bool catalog = args[1] == "--catalog-ui";
        uint appId = 0;
        uint[] ids;
        int samples = 1, parent = 0;
        string action = null, prefix = null;
        bool checkOnly = false;
        if (catalog)
        {
            if (args.Length < 3 || args.Length > 5) return 2;
            try { ids = args[2] == "-" ? new uint[0] : args[2].Split(',').Select(value => uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)).ToArray(); }
            catch (FormatException) { return 2; }
            catch (OverflowException) { return 2; }
            if (args.Length > 3 && !int.TryParse(args[3], out samples) || args.Length > 4 && !int.TryParse(args[4], out parent)) return 2;
        }
        else
        {
            if (!uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out appId) || appId == 0) return 2;
            ids = new[] { appId };
            if (args.Length >= 3 && args[2].StartsWith("--", StringComparison.Ordinal))
            {
                switch (args[2])
                {
                    case "--ui-select-update": case "--ui-check-select-update": action = "select"; prefix = "U2"; break;
                    case "--ui-close-update": case "--ui-check-close-update": action = "close"; prefix = "U3"; break;
                    case "--ui-downloads-command":
                    case "--ui-selected-downloads-command":
                        if (args.Length != 4 || args[3] != "pause" && args[3] != "resume") return 2;
                        action = args[3]; prefix = "U1"; break;
                    default: return 2;
                }
                if (prefix != "U1" && args.Length != 3) return 2;
                checkOnly = args[2].Contains("-check-");
            }
            else
            {
                if (args.Length > 5 || args.Length > 2 && !int.TryParse(args[2], out samples) ||
                    args.Length > 3 && args[3] != "--protocol" || args.Length > 4 && !int.TryParse(args[4], out parent)) return 2;
            }
        }
        if (ids.Length > 1024 || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Length || samples < 1 || samples > 86400 || parent < 0) return 2;
        try
        {
            using (var reader = new SteamUiClient(args[0]))
            {
                if (action != null) return Command(reader, ids, appId, action, prefix, checkOnly);
                for (int tick = 1; tick <= samples; tick++)
                {
                    if (parent > 0 && !ParentAlive(parent)) break;
                    var snapshot = reader.Read(ids);
                    if (catalog)
                    {
                        var rows = ids.Select(id =>
                        {
                            var sample = reader.Sample(snapshot, id);
                            var app = snapshot.Apps.Single(row => row.AppId == id);
                            int index = app.ItemPresent ? app.QueueIndex ?? -2 : -1;
                            return id + ":" + (sample.InstallState == 0 || index < -1 ? "-:-" : sample.InstallState + ":" + index);
                        }).ToArray();
                        string progress = "-";
                        if (snapshot.ActiveAppId.HasValue && snapshot.ActiveAppId != 0 && ids.Contains(snapshot.ActiveAppId.Value))
                        {
                            var sample = reader.Sample(snapshot, snapshot.ActiveAppId.Value);
                            if (sample.HasCounters) progress = sample.AppId + ":" + sample.InstallState + ":" + sample.Payload();
                        }
                        Console.WriteLine("Q3|" + (snapshot.DownloadsEnabled.HasValue && snapshot.ActiveAppId.HasValue && snapshot.QueueCount.HasValue ?
                            (snapshot.DownloadsEnabled.Value ? "1" : "0") + "|" + snapshot.ActiveAppId + "|" + snapshot.QueueCount : "-|-|-") +
                            "|" + tick + "|" + (rows.Length == 0 ? "-" : string.Join(",", rows)) + "|" + progress);
                    }
                    else Console.WriteLine(reader.Sample(snapshot, appId).Frame());
                    Console.Out.Flush();
                    if (tick < samples) Thread.Sleep(1000);
                }
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Steam UI connection unavailable: " + exception.GetType().Name + ": " + exception.Message);
            return 1;
        }
    }

    private static int Command(SteamUiClient reader, uint[] ids, uint appId, string action, string prefix, bool checkOnly)
    {
        var before = reader.Read(ids);
        var sample = reader.Sample(before, appId);
        const uint pending = 2 | 32 | 128 | 256 | 512 | 1024 | 65536 | 131072 | 262144 | 524288 | 1048576 | 2097152 | 4194304 | 8388608;
        bool known = sample.InstallState != 0 && (sample.InstallState & ~16777215u) == 0;
        bool running = (sample.InstallState & (8192 | 2048 | 4096 | 32768)) != 0;
        bool complete = known && !running && (sample.InstallState & 4) != 0 && (sample.InstallState & (pending | 8)) == 0;
        if (!before.Controls || !before.DownloadsEnabled.HasValue || !known || running) return 1;
        string status;
        if (action == "select" && complete) status = "finished";
        else if ((action == "select" || action == "resume") && !before.Apps.Single(app => app.AppId == appId).ItemPresent) status = "waiting";
        else if (checkOnly) status = "ready";
        else
        {
            if (SteamLocator.IsAppRunning(appId)) return 1;
            var after = reader.Read(ids, action, appId);
            status = after.Command == "accepted" ? "accepted" : "rejected";
        }
        Console.WriteLine(prefix + "|" + appId + "|" + action + "|" + status);
        return status == "rejected" ? 1 : 0;
    }

    private static bool ParentAlive(int id)
    {
        try { using (var parent = Process.GetProcessById(id)) return !parent.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
