using System;
using System.Globalization;

namespace PlayniteUpdateStatus.Core
{
    public enum UpdatePhase { Idle, Launching, Updating, Ready, Running, Error }
    public enum SpeedUnit { BytesPerSecond, BitsPerSecond }

    public sealed class UpdateDisplay
    {
        public ulong? Downloaded { get; set; }
        public ulong? DownloadTotal { get; set; }
        public int? DownloadPercent { get; set; }
        public string Stage { get; set; }
        public int? WorkPercent { get; set; }
        public bool Paused { get; set; }
        public bool WaitingForData { get; set; }
        public double? BytesPerSecond { get; set; }
        public int? SecondsRemaining { get; set; }
        public string Notice { get; set; }
        public string DownloadText
        {
            get
            {
                if (!Downloaded.HasValue || !DownloadTotal.HasValue || DownloadTotal == 0) return string.Empty;
                // Steam uses binary units labelled KB/MB/GB. Match its one-decimal GB display.
                return (Downloaded == DownloadTotal ? "Downloading complete — " : "Downloading data — ") + Bytes(Downloaded.Value) + " / " + Bytes(DownloadTotal.Value);
            }
        }
        public string StageText { get { return (Stage ?? "Updating files") + (WorkPercent.HasValue ? " — " + WorkPercent.Value + "%" : "…"); } }
        public string MetricsText
        {
            get { return GetMetricsText(SpeedUnit.BytesPerSecond); }
        }
        public string GetMetricsText(SpeedUnit unit)
        {
                if (!string.IsNullOrEmpty(Notice)) return Notice;
                if (WaitingForData) return "Waiting for update data…";
                if (Paused) return "Download paused";
                string speed = BytesPerSecond.HasValue ? FormatSpeed(BytesPerSecond.Value,unit) : string.Empty;
                string time = SecondsRemaining.HasValue && SecondsRemaining > 0 ? "Estimated " + Duration(SecondsRemaining.Value) + " remaining" : string.Empty;
                return speed.Length > 0 && time.Length > 0 ? speed + " · " + time : speed + time;
        }
        public static string FormatSpeed(double bytesPerSecond, SpeedUnit unit)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0 || bytesPerSecond >= 18446744073709551616d) return string.Empty;
            bool bits = unit == SpeedUnit.BitsPerSecond;
            decimal value = (decimal)bytesPerSecond * (bits ? 8 : 1);
            decimal scale = bits ? 1000 : 1024;
            string[] units = bits ? new[] { "kbps", "Mbps", "Gbps", "Tbps", "Pbps", "Ebps" } : new[] { "KB/s", "MB/s", "GB/s", "TB/s", "PB/s", "EB/s" };
            value /= scale; int index = 0;
            while (value >= scale && index < units.Length-1) { value /= scale; index++; }
            return value.ToString("0.0",CultureInfo.CurrentCulture) + " " + units[index];
        }
        private static string Duration(int seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return seconds >= 3600 ? ((int)time.TotalHours).ToString(CultureInfo.CurrentCulture) + ":" + time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00") : time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00");
        }
        private static string Bytes(ulong bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            decimal value = bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    public sealed class LaunchIdentity
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string LauncherKey { get; set; }
        public string LauncherName { get; set; }
        public string LauncherId { get; set; }
        public string InstallDirectory { get; set; }
    }

    public sealed class UpdateObservation
    {
        public bool? CloseConfirmed { get; set; }
        public bool? ControlPaused { get; set; }
        public string ControlScope { get; set; }
        public bool CanPause { get; set; }
        public bool CanResume { get; set; }
        public UpdatePhase Phase { get; set; }
        public int? Percent { get; set; }
        public UpdateDisplay Display { get; set; }
        public bool CompletionPending { get; set; }
        public string Detail { get; set; }
    }

    public sealed class UpdateState
    {
        public long SessionId { get; set; }
        public bool Dismissed { get; set; }
        public bool? ControlPaused { get; set; }
        public string ControlScope { get; set; }
        public bool CanPause { get; set; }
        public bool CanResume { get; set; }
        public bool CommandPending { get; set; }
        public string CommandNotice { get; set; }
        public long Revision { get; set; }
        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string LauncherName { get; set; }
        public UpdatePhase Phase { get; set; }
        public int? Percent { get; set; }
        public UpdateDisplay Display { get; set; }
        public string Detail { get; set; }
        public string Text
        {
            get
            {
                if (Phase == UpdatePhase.Updating)
                    return Display == null ? "Updating…" : (Display.DownloadText.Length == 0 ? Display.StageText : Display.DownloadText + Environment.NewLine + Display.StageText);
                if (Phase == UpdatePhase.Ready) return "Update complete — 100%";
                if (Phase == UpdatePhase.Launching) return "Launching…";
                if (Phase == UpdatePhase.Error) return "Game update interrupted — check the launcher";
                return string.Empty;
            }
        }
        public bool Visible { get { return Phase != UpdatePhase.Idle && Phase != UpdatePhase.Running; } }
    }
}
