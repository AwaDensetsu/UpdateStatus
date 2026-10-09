using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    public sealed class CoverBadgeInfo
    {
        public Guid GameId { get; private set; }
        public string Text { get; private set; }
        public int? Percent { get; private set; }
        public string CoverText { get; private set; }
        public int? CoverPercent { get; private set; }
        public CoverBadgeInfo(Guid gameId, string text) : this(gameId,text,null) { }
        public CoverBadgeInfo(Guid gameId, string text, int? percent) : this(gameId,text,percent,null) { }
        public CoverBadgeInfo(Guid gameId, string text, int? percent, UpdateDisplay display)
        {
            GameId = gameId; Text = text; Percent = text == "Updating" && percent >= 0 && percent <= 100 ? percent : null;
            // All library surfaces use this measured download/stage presentation.
            // Legacy Text/Percent values remain available to existing callers;
            // work progress is never displayed as a total update percentage.
            CoverText = text == "Updating" ? CoverStage(display) : text ?? "Updating";
            if (text == "Updating" && display != null && !display.WaitingForData && display.Downloaded.HasValue && display.DownloadTotal.HasValue &&
                display.DownloadTotal.Value > 0 && display.Downloaded.Value < display.DownloadTotal.Value)
            {
                CoverText = "Downloading";
                CoverPercent = SteamLivePresentation.Ratio(display.Downloaded.Value,display.DownloadTotal.Value);
            }
        }
        private static string CoverStage(UpdateDisplay display)
        {
            if (display == null || display.WaitingForData) return "Updating";
            switch (display.Stage)
            {
                case "Updating files": case "Patching files": case "Installing files": case "Verifying files":
                case "Validating files": case "Reserving space": case "Unpacking files": case "Starting download":
                case "Preloading": case "Finalizing": case "Stopping": case "Queued": return display.Stage;
                default: return "Updating";
            }
        }
    }
    public sealed class CoverBadgeSnapshot
    {
        private readonly SteamCatalogSnapshot catalog;
        private readonly HashSet<uint> remembered = new HashSet<uint>();
        private readonly DateTime received;
        private readonly UpdateState selected;
        public CoverBadgeSnapshot(SteamCatalogSnapshot catalog, SteamCoverHistory history = null) : this(catalog,history,null) { }
        public CoverBadgeSnapshot(SteamCatalogSnapshot catalog, SteamCoverHistory history, UpdateState selected)
        {
            this.selected = selected;
            if (history != null) history.CopyPendingTo(remembered);
            if (catalog == null || !catalog.Fresh) return;
            received = catalog.ReceivedUtc;
            this.catalog = catalog;
        }
        public CoverBadgeInfo Find(Game game)
        {
            if (game == null || game.IsRunning) return null;
            // Cover identity must come from the importing library, never its title
            // or image filename. Manual/custom launch actions remain unclassified.
            uint appId;
            if (game.PluginId != SteamGameIdentity.LibraryId || !uint.TryParse(game.GameId,NumberStyles.None,CultureInfo.InvariantCulture,out appId)) return null;
            var now = DateTime.UtcNow;
            bool fresh = catalog != null && received <= now && now-received <= TimeSpan.FromSeconds(5);
            var entry = fresh ? catalog.Find(appId) : null;
            if (entry != null && entry.IsRunning) return null;
            if (selected != null && selected.GameId == game.Id && !selected.Dismissed)
            {
                if (selected.Phase == UpdatePhase.Ready || selected.Phase == UpdatePhase.Running) return null;
                if (selected.Phase == UpdatePhase.Updating)
                {
                    var display = selected.Display;
                    bool paused = selected.ControlPaused ?? (display != null && display.Paused);
                    int? percent = display != null && display.WaitingForData ? null : display == null ? selected.Percent : display.WorkPercent;
                    return new CoverBadgeInfo(game.Id,paused ? "Paused" : "Updating",percent,display);
                }
            }
            if (entry == null || !entry.RequiresUpdate.HasValue || entry.Status == SteamCatalogStatus.Unknown)
                return remembered.Contains(appId) ? new CoverBadgeInfo(game.Id,"Update Required") : null;
            if (entry.IsRunning) return null;
            string text;
            switch (entry.Status)
            {
                case SteamCatalogStatus.Updating: text = "Updating"; break;
                case SteamCatalogStatus.Paused: text = "Update Required"; break;
                case SteamCatalogStatus.Queued: text = "Update Required"; break;
                case SteamCatalogStatus.RequiresUpdate: text = "Update Required"; break;
                case SteamCatalogStatus.RepairRequired: text = entry.RequiresUpdate == true ? "Update Required" : null; break;
                default: return null;
            }
            if (text == null) return null;
            var background = text == "Updating" ? catalog.DisplayForActiveApp(appId) : null;
            return background != null && background.Paused ? new CoverBadgeInfo(game.Id,"Update Required") :
                new CoverBadgeInfo(game.Id,text,null,background);
        }
    }
    public static class CoverGameContext
    {
        private static readonly Dictionary<Type,PropertyInfo> properties = new Dictionary<Type,PropertyInfo>();
        public static Game Resolve(object context)
        {
            for (int depth = 0; context != null && depth < 4; depth++)
            {
                var game = context as Game; if (game != null) return game;
                PropertyInfo property;
                lock (properties)
                {
                    if (!properties.TryGetValue(context.GetType(),out property))
                    {
                        property = context.GetType().GetProperty("Game",BindingFlags.Public | BindingFlags.Instance);
                        if (property != null && (!property.CanRead || property.GetIndexParameters().Length != 0)) property = null;
                        properties[context.GetType()] = property;
                    }
                }
                if (property == null) return null;
                try { context = property.GetValue(context,null); }
                catch (TargetInvocationException) { return null; } catch (MethodAccessException) { return null; }
            }
            return null;
        }
    }
}
