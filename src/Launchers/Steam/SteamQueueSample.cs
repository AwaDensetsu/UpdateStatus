using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public sealed class SteamQueueItem
    {
        public uint AppId { get; private set; }
        public uint? Flags { get; private set; }
        public int? QueueIndex { get; private set; }
        public SteamQueueItem(uint appId, uint? flags, int? index) { AppId = appId; Flags = flags; QueueIndex = index; }
    }
    public sealed class SteamQueueSample
    {
        public bool? DownloadsEnabled { get; private set; }
        public uint? ActiveAppId { get; private set; }
        public uint? QueueCount { get; private set; }
        public uint Sequence { get; private set; }
        public IList<SteamQueueItem> Items { get; private set; }
        public SteamLiveSample ActiveUpdate { get; private set; }
        public DateTime ReceivedUtc { get; set; }
        private static uint Number(string value)
        {
            uint number;
            if (!uint.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out number)) throw new FormatException("Invalid Steam catalog number");
            return number;
        }
        public static SteamQueueSample Parse(string line, IEnumerable<uint> expectedIds)
        {
            if (line == null || line.Length > 65536) throw new FormatException("Invalid Steam catalog frame length");
            var fields = line.Split('|');
            bool progressProtocol = fields.Length == 7 && fields[0] == "Q3";
            if (!progressProtocol) throw new FormatException("Invalid Steam catalog protocol");
            var sample = new SteamQueueSample();
            bool unknown = fields[1] == "-";
            if (unknown) { if (fields[2] != "-" || fields[3] != "-") throw new FormatException("Inconsistent catalog globals"); }
            else
            {
                if (fields[1] != "0" && fields[1] != "1") throw new FormatException("Invalid catalog download state");
                sample.DownloadsEnabled = fields[1] == "1"; sample.ActiveAppId = Number(fields[2]); sample.QueueCount = Number(fields[3]);
                if (sample.QueueCount > 100000) throw new FormatException("Invalid catalog queue size");
            }
            sample.Sequence = Number(fields[4]); if (sample.Sequence == 0) throw new FormatException("Invalid catalog sequence");
            var expected = new HashSet<uint>(expectedIds); var seen = new HashSet<uint>(); var items = new List<SteamQueueItem>();
            if (expected.Count > 1024 || expected.Contains(0)) throw new FormatException("Invalid expected catalog identities");
            foreach (string row in fields[5] == "-" ? new string[0] : fields[5].Split(','))
            {
                var parts = row.Split(':'); if (parts.Length != 3) throw new FormatException("Invalid catalog row");
                uint id = Number(parts[0]); if (!expected.Contains(id) || !seen.Add(id)) throw new FormatException("Mismatched/duplicate catalog identity");
                uint? flags = null; int? index = null;
                if (parts[1] == "-") { if (parts[2] != "-") throw new FormatException("Inconsistent catalog row"); }
                else
                {
                    flags = Number(parts[1]); int value;
                    if (!int.TryParse(parts[2],NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out value) || value < -1 || value > 100000)
                        throw new FormatException("Invalid catalog queue index");
                    index = value;
                }
                items.Add(new SteamQueueItem(id,flags,index));
            }
            if (!seen.SetEquals(expected)) throw new FormatException("Missing catalog identities");
            // The queue may reorder without changing its count/active app. Duplicate
            // positions prove that this frame's ordering is mixed; retain flags only.
            var positions = items.Where(item => sample.QueueCount.HasValue && item.QueueIndex >= 0 && item.QueueIndex < sample.QueueCount).Select(item => item.QueueIndex.Value).ToList();
            if (positions.Distinct().Count() != positions.Count) items = items.Select(item => new SteamQueueItem(item.AppId,item.Flags,null)).ToList();
            sample.Items = items.AsReadOnly();
            if (progressProtocol && !unknown && sample.ActiveAppId != 0 && fields[6] != "-")
            {
                // Optional numeric data cannot invalidate otherwise valid queue
                // state. Require matching active identity, flags and global Pause.
                try
                {
                    var parts = fields[6].Split(':');
                    if (parts.Length == 3)
                    {
                        var live = SteamLiveSample.Parse("S4|" + parts[0] + "|" + parts[1] + "|" + parts[2],sample.ActiveAppId.Value);
                        var row = items.FirstOrDefault(item => item.AppId == live.AppId);
                        if (live.HasCounters && row != null && row.Flags == live.InstallState && live.DownloadsEnabled == sample.DownloadsEnabled &&
                            SteamSelectionPolicy.Known(live.InstallState) && (live.InstallState & 8192) == 0)
                            sample.ActiveUpdate = live;
                    }
                }
                catch (FormatException) { }
            }
            return sample;
        }
    }
}
