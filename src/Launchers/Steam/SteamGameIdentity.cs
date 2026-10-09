using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    public static class SteamGameIdentity
    {
        public static readonly Guid LibraryId = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");
        public static LaunchIdentity Resolve(Game game)
        {
            if (game == null) return null;
            uint appId;
            // Imported Steam game GameId is an AppID, not its Playnite database GUID.
            if (game.PluginId == LibraryId && uint.TryParse(game.GameId, NumberStyles.None, CultureInfo.InvariantCulture, out appId) && appId > 0)
                return Identity(game, appId);
            // Manual games: only an unambiguous explicit play URI or steam.exe -applaunch.
            var ids = (game.GameActions ?? new System.Collections.ObjectModel.ObservableCollection<GameAction>())
                .Where(a => a.IsPlayAction).Select(a => ActionId(a)).Where(id => id > 0).Distinct().ToList();
            return ids.Count == 1 ? Identity(game, ids[0]) : null;
        }
        private static uint ActionId(GameAction action)
        {
            string path = action.Path ?? "";
            var uri = Regex.Match(path, @"^steam://(?:rungameid|run)/([0-9]+)(?:/.*)?$", RegexOptions.IgnoreCase);
            uint id;
            if (uri.Success && uint.TryParse(uri.Groups[1].Value, out id)) return id;
            if (System.IO.Path.GetFileName(path.Trim('"')).Equals("steam.exe", StringComparison.OrdinalIgnoreCase))
            {
                var args = Regex.Match(action.Arguments ?? "", @"(?:^|\s)-applaunch\s+([0-9]+)(?:\s|$)", RegexOptions.IgnoreCase);
                if (args.Success && uint.TryParse(args.Groups[1].Value, out id)) return id;
            }
            return 0;
        }
        private static LaunchIdentity Identity(Game game, uint appId)
        { return new LaunchIdentity { GameId = game.Id, Name = game.Name, LauncherId = appId.ToString(CultureInfo.InvariantCulture), InstallDirectory = game.InstallDirectory }; }
    }
}
