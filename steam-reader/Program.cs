using System;
using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.Globalization;
using PlayniteUpdateStatus.Launchers.Steam;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("Usage: SteamUpdateReader.exe SteamRoot AppID [samples=1]"); return 2; }
        uint appId; int samples = 1;
        if (args.Length == 3 && (args[2] == "--close-update" || args[2] == "--check-close-update"))
        {
            if (!uint.TryParse(args[1],out appId) || appId == 0) return 2;
            try
            {
                if (!NativeSteamReader.SupportsControl(args[0])) throw new NotSupportedException("Unverified Steam frontend; close refused");
                using (var reader = new NativeSteamReader(args[0]))
                {
                    reader.ValidateSelection();
                    uint before = reader.State(appId); int index = reader.QueueIndex(appId); uint active = reader.DownloadingApp();
                    bool enabled = reader.DownloadsEnabled(); uint after = reader.State(appId);
                    if (before != after || index < -1 || !SteamSelectionPolicy.CanClose(after))
                        throw new InvalidOperationException("Fresh close state unavailable or game running; no command sent");
                    bool queued = index >= 0 || active == appId || (after & (8 | 256)) != 0;
                    if (!enabled && !queued) { Console.WriteLine("C6|" + appId + "|close|already"); return 0; }
                    if (args[2] == "--check-close-update") { Console.WriteLine("C6|" + appId + "|close|ready"); return 0; }
                    // Keep the user-selected global Pause behavior. Remove only this
                    // app using SteamUI's own RemoveFromDownloadList placement 6.
                    if (enabled && !reader.Control(true)) throw new InvalidOperationException("Global pause rejected; queue removal not sent");
                    uint fresh = reader.State(appId);
                    if (!SteamSelectionPolicy.CanClose(fresh))
                        throw new InvalidOperationException("Game state changed after pause; queue removal not sent");
                    int freshIndex = reader.QueueIndex(appId);
                    if (freshIndex < -1) throw new NotSupportedException("Unexpected native queue index; removal not sent");
                    bool stillQueued = freshIndex >= 0 || reader.DownloadingApp() == appId || (fresh & (8 | 256)) != 0;
                    bool accepted = !stillQueued || reader.RemoveUpdate(appId);
                    Console.WriteLine("C6|" + appId + "|close|" + (accepted ? "accepted" : "rejected"));
                    return accepted ? 0 : 1;
                }
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (args.Length == 3 && (args[2] == "--select-update" || args[2] == "--check-select-update"))
        {
            if (!uint.TryParse(args[1],out appId) || appId == 0) return 2;
            try
            {
                if (!NativeSteamReader.SupportsControl(args[0])) throw new NotSupportedException("Unverified Steam frontend; selection refused");
                using (var reader = new NativeSteamReader(args[0]))
                {
                    reader.ValidateSelection();
                    uint before = reader.State(appId); var info = reader.Update(appId); uint after = reader.State(appId);
                    if (before != after || !SteamSelectionPolicy.Known(after)) { Console.WriteLine("C4|" + appId + "|select|waiting"); return 0; }
                    if (SteamSelectionPolicy.Complete(after)) { Console.WriteLine("C4|" + appId + "|select|finished"); return 0; }
                    if (!SteamSelectionPolicy.Pending(after) || info != null && (BitConverter.ToUInt32(info,4) & ~2039U) != 0)
                        throw new InvalidOperationException("Game is running or selection state is unsupported");
                    bool enabled = reader.DownloadsEnabled();
                    bool active = reader.DownloadingApp() == appId && (after & 256) != 0 && (after & 512) == 0;
                    if (enabled && active) { Console.WriteLine("C4|" + appId + "|select|already"); return 0; }
                    if (args[2] == "--check-select-update") { Console.WriteLine("C4|" + appId + "|select|ready"); return 0; }
                    // Validate all pointers before prioritizing the selected update.
                    // The plugin handles removal of any replaced selection beforehand.
                    if (reader.State(appId) != after) { Console.WriteLine("C4|" + appId + "|select|waiting"); return 0; }
                    bool accepted = reader.SelectUpdate(appId,enabled,active);
                    Console.WriteLine("C4|" + appId + "|select|" + (accepted ? "accepted" : "rejected"));
                    return accepted ? 0 : 1;
                }
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (args[1] == "--catalog")
        {
            int parent = 0;
            if (args.Length < 3 || args.Length > 5 || args.Length > 3 && (!int.TryParse(args[3],out samples) || samples < 1 || samples > 86400) ||
                args.Length > 4 && (!int.TryParse(args[4],out parent) || parent < 1)) return 2;
            uint[] ids;
            try { ids = args[2] == "-" ? new uint[0] : args[2].Split(',').Select(s => uint.Parse(s,NumberStyles.None,CultureInfo.InvariantCulture)).ToArray(); }
            catch (Exception) { return 2; }
            if (ids.Length > 1024 || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Length) return 2;
            try
            {
                using (var reader = new NativeSteamReader(args[0]))
                {
                    reader.ValidateQueue();
                    for (int tick = 1; tick <= samples; tick++)
                    {
                        if (parent > 0) { try { using (var process = Process.GetProcessById(parent)) if (process.HasExited) break; } catch (ArgumentException) { break; } }
                        bool enabled = reader.DownloadsEnabled(); uint active = reader.DownloadingApp(), count = reader.QueueCount();
                        var rows = ids.Select(id => {
                            uint before = reader.State(id); int index = reader.QueueIndex(id); uint after = reader.State(id);
                            return id.ToString(CultureInfo.InvariantCulture) + ":" + (before == after ? after.ToString(CultureInfo.InvariantCulture) + ":" + index.ToString(CultureInfo.InvariantCulture) : "-:-");
                        }).ToArray();
                        string progress = "-";
                        // One getter for the active known AppID, in the existing
                        // catalog helper. No per-game helpers or setters are used.
                        if (active != 0 && ids.Contains(active))
                        {
                            uint before = reader.State(active);
                            byte[] update = reader.Update(active);
                            uint after = reader.State(active);
                            if (before == after && update != null)
                                progress = active.ToString(CultureInfo.InvariantCulture) + ":" + after.ToString(CultureInfo.InvariantCulture) + ":" + SteamUpdatePayload.Encode(update,enabled);
                        }
                        bool consistent = enabled == reader.DownloadsEnabled() && active == reader.DownloadingApp() && count == reader.QueueCount();
                        Console.WriteLine("Q2|" + (consistent ? enabled ? "1" : "0" : "-") + "|" + (consistent ? active.ToString(CultureInfo.InvariantCulture) : "-") + "|" +
                            (consistent ? count.ToString(CultureInfo.InvariantCulture) : "-") + "|" + tick + "|" + (rows.Length == 0 ? "-" : string.Join(",",rows)) + "|" + (consistent ? progress : "-"));
                        Console.Out.Flush();
                        if (tick < samples) Thread.Sleep(1000);
                    }
                }
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (args.Length == 4 && (args[2] == "--downloads-command" || args[2] == "--check-downloads-command" || args[2] == "--selected-downloads-command"))
        {
            if (!uint.TryParse(args[1], out appId) || appId == 0 || (args[3] != "pause" && args[3] != "resume")) return 2;
            try
            {
                if (!NativeSteamReader.SupportsControl(args[0])) throw new NotSupportedException("Unverified Steam frontend; update commands refused");
                using (var reader = new NativeSteamReader(args[0]))
                {
                    uint before = reader.State(appId);
                    var info = reader.Update(appId);
                    uint after = reader.State(appId);
                    bool selected = args[2] == "--selected-downloads-command";
                    if (before != after || !selected && info == null)
                        throw new InvalidOperationException("Game is running, finished or update state changed; command not sent");
                    uint stage = info == null ? 0 : BitConverter.ToUInt32(info, 4), result = info == null ? 0 : BitConverter.ToUInt32(info, 60);
                    bool paused = !reader.DownloadsEnabled();
                    if (selected ? !(SteamSelectionPolicy.Pending(after) || SteamSelectionPolicy.Complete(after)) || (stage & ~2039U) != 0 : !SteamControlPolicy.Available(after,stage,result,true))
                        throw new InvalidOperationException("Update control state unavailable; command not sent");
                    bool pause = args[3] == "pause";
                    reader.ValidateControl();
                    string frame = (selected ? "C5|" : "C2|") + appId + "|" + args[3] + "|";
                    if (pause == paused) { Console.WriteLine(frame + "already"); return 0; }
                    if (args[2] == "--check-downloads-command") { Console.WriteLine("C2|" + appId + "|" + args[3] + "|ready"); return 0; }
                    bool accepted = reader.Control(pause);
                    Console.WriteLine(frame + (accepted ? "accepted" : "rejected"));
                    return accepted ? 0 : 1;
                }
            }
            catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
        }
        if (!uint.TryParse(args[1], out appId) || appId == 0 || args.Length > 2 && !int.TryParse(args[2], out samples) || samples < 1 || samples > 86400) return 2;
        bool protocol = args.Length > 3 && args[3] == "--protocol";
        int parentId = 0;
        if (args.Length > 4 && (!int.TryParse(args[4], out parentId) || parentId < 1)) return 2;
        try
        {
            using (var reader = new NativeSteamReader(args[0]))
            {
                for (int i = 0; i < samples; i++)
                {
                    if (parentId > 0) { try { using (var parent = Process.GetProcessById(parentId)) if (parent.HasExited) break; } catch (ArgumentException) { break; } }
                    uint before = reader.State(appId);
                    byte[] result = reader.Update(appId);
                    uint after = reader.State(appId);
                    if (before != after) result = null;
                    string hex = result == null ? "-" : BitConverter.ToString(result).Replace("-", "");
                    if (protocol)
                    {
                        if (result != null)
                        {
                            bool enabled = reader.DownloadsEnabled();
                            uint networkAppId;
                            uint? network = reader.NetworkSpeed(out networkAppId);
                            hex = SteamUpdatePayload.Encode(result,enabled,network,networkAppId);
                        }
                        Console.WriteLine("S3|" + appId + "|" + after + "|" + hex);
                    }
                    else { Console.WriteLine("AppID=" + appId + " state=" + after); Console.WriteLine("UpdateInfo=" + hex); }
                    Console.Out.Flush();
                    if (i + 1 < samples) Thread.Sleep(1000);
                }
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e.GetType().Name + ": " + e.Message); return 1; }
    }
}

// Observation binds only getters. Only explicit command modes bind setters.
