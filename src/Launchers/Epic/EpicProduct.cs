using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.UI;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public sealed class EpicProduct
    {
        public string App { get; }
        public string Namespace { get; }
        public string Catalog { get; }
        public bool Initialized { get; }
        public bool Installed { get; }
        public bool? HasUpdate { get; }
        public bool? Updating { get; }
        public bool? Queued { get; }
        public bool? Running { get; }
        public EpicProduct(string app, string productNamespace, string catalog, bool initialized,
            bool installed, bool? hasUpdate, bool? updating, bool? queued, bool? running)
        {
            App = app; Namespace = productNamespace; Catalog = catalog;
            Initialized = initialized; Installed = installed; HasUpdate = hasUpdate;
            Updating = updating; Queued = queued; Running = running;
        }
        public bool NeedsAttention => Initialized && Running == false &&
            (Updating == true || Queued == true || HasUpdate == true);
    }

    public sealed class EpicSnapshot
    {
        public static readonly Guid LibraryId = new Guid("00000002-DBD1-46C6-B5D0-B1BA559D10E4");
        private readonly Dictionary<string, EpicProduct> products;
        public DateTime ReceivedUtc { get; }
        public string Status { get; }
        public bool Connected { get; }
        public EpicConnectionFailure Failure { get; }
        public bool Fresh => Connected && ReceivedUtc <= DateTime.UtcNow &&
            DateTime.UtcNow - ReceivedUtc < TimeSpan.FromSeconds(5);
        public EpicSnapshot(DateTime received, IEnumerable<EpicProduct> values, bool connected, string status, EpicConnectionFailure failure = EpicConnectionFailure.None)
        {
            ReceivedUtc = received; Connected = connected && failure == EpicConnectionFailure.None; Status = status; Failure = failure;
            products = new Dictionary<string, EpicProduct>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value.App) || string.IsNullOrWhiteSpace(value.Namespace) ||
                    string.IsNullOrWhiteSpace(value.Catalog)) continue;
                // Playnite stores AppName only. Different triples must not map to one game.
                EpicProduct existing;
                if (products.TryGetValue(value.App, out existing) && (existing == null ||
                    existing.Namespace != value.Namespace || existing.Catalog != value.Catalog))
                    products[value.App] = null;
                else products[value.App] = value;
            }
        }
        public EpicProduct Find(string app)
        {
            EpicProduct product;
            return Fresh && app != null && products.TryGetValue(app, out product) ? product : null;
        }
        private static bool HasIdentity(Game game) =>
            game != null && game.PluginId == LibraryId && !string.IsNullOrWhiteSpace(game.GameId);
        public static LaunchIdentity Resolve(Game game)
        {
            if (!HasIdentity(game)) return null;
            return new LaunchIdentity { GameId = game.Id, Name = game.Name, LauncherId = game.GameId,
                InstallDirectory = game.InstallDirectory };
        }
        public CoverBadgeInfo Badge(Game game)
        {
            if (!HasIdentity(game) || game.IsRunning) return null;
            var product = Find(game.GameId);
            if (product == null || !product.NeedsAttention) return null;
            return new CoverBadgeInfo(game.Id, product.Updating == true ? "Updating" : "Update Required");
        }
        public static EpicSnapshot Parse(string json, DateTime received)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1048576 };
            var document = serializer.DeserializeObject(json) as Dictionary<string, object>;
            object raw;
            if (document == null || !document.TryGetValue("products", out raw) || !(raw is object[]))
                throw new InvalidDataException("Invalid Epic product response");
            var result = new List<EpicProduct>();
            foreach (var item in (object[])raw)
            {
                var value = item as Dictionary<string, object>;
                if (value == null) throw new InvalidDataException("Invalid Epic product");
                result.Add(new EpicProduct(Text(value,"app"), Text(value,"namespace"), Text(value,"catalog"),
                    Flag(value,"initialized") == true, Flag(value,"installed") == true,
                    Flag(value,"hasUpdate"), Flag(value,"updating"), Flag(value,"queued"), Flag(value,"running")));
            }
            return new EpicSnapshot(received, result, true, "Connected — observation beta");
        }
        private static string Text(Dictionary<string, object> value, string key)
        { object raw; return value.TryGetValue(key, out raw) ? raw as string : null; }
        private static bool? Flag(Dictionary<string, object> value, string key)
        { object raw; return value.TryGetValue(key, out raw) && raw is bool ? (bool?)raw : null; }
    }
}
