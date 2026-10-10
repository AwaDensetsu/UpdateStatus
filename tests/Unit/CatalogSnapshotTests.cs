using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Steam;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class CatalogSnapshotTests
    {
        private static SteamUpdateCatalog Catalog()
        {
            return new SteamUpdateCatalog(() => null,
                () => { Assert.Fail("Snapshot reads must not discover Steam"); return null; },
                apps => { Assert.Fail("Snapshot reads must not open a Steam reader"); return null; },
                (message, warning) => { });
        }

        private static void Publish(SteamUpdateCatalog catalog, uint id, DateTime received)
        {
            var apps = new List<SteamCatalogApp> { new SteamCatalogApp(id, "Synthetic app " + id) }.AsReadOnly();
            var sample = SteamQueueSample.Parse("Q3|1|" + id + "|1|1|" + id + ":270:0|-", new[] { id });
            sample.ReceivedUtc = received;
            // Publish a controlled worker result without starting timers or IPC.
            typeof(SteamUpdateCatalog).GetField("apps", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(catalog, apps);
            typeof(SteamUpdateCatalog).GetField("current", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(catalog, new SteamCatalogSnapshot(apps, sample, null));
        }

        [Test]
        public void ExpiredCurrentKeepsInventoryWithoutExposingStaleLiveGlobals()
        {
            using (var catalog = Catalog())
            {
                Publish(catalog, 1, DateTime.UtcNow.AddMinutes(-1));
                var expired = catalog.Current;
                Assert.That(expired.Live, Is.False);
                Assert.That(expired.Fresh, Is.False);
                Assert.That(expired.DownloadsEnabled, Is.Null);
                Assert.That(expired.ActiveAppId, Is.Null);
                Assert.That(expired.QueueCount, Is.Null);
                Assert.That(expired.Find(1).Name, Is.EqualTo("Synthetic app 1"));
                Assert.That(expired.Find(1).Status, Is.EqualTo(SteamCatalogStatus.Unknown));
            }
        }

        [Test]
        public void ANewExpiredSnapshotUsesTheLatestInventory()
        {
            using (var catalog = Catalog())
            {
                Publish(catalog, 1, DateTime.UtcNow.AddMinutes(-1));
                Assert.That(catalog.Current.Find(1), Is.Not.Null);
                Publish(catalog, 2, DateTime.UtcNow.AddMinutes(-1));
                var next = catalog.Current;
                Assert.That(next.Find(1), Is.Null);
                Assert.That(next.Find(2).Name, Is.EqualTo("Synthetic app 2"));
                Assert.That(next.Entries.Count, Is.EqualTo(1));
            }
        }

        [Test]
        public void FreshRecoveryReplacesUnavailableCatalogData()
        {
            using (var catalog = Catalog())
            {
                Publish(catalog, 1, DateTime.UtcNow.AddMinutes(-1));
                Assert.That(catalog.Current.Live, Is.False);
                Publish(catalog, 1, DateTime.UtcNow);
                var recovered = catalog.Current;
                Assert.That(recovered.Live, Is.True);
                Assert.That(recovered.Fresh, Is.True);
                Assert.That(recovered.DownloadsEnabled, Is.True);
                Assert.That(recovered.ActiveAppId, Is.EqualTo(1));
                Assert.That(recovered.Find(1).Status, Is.EqualTo(SteamCatalogStatus.Updating));
            }
        }

        [Test]
        public void StopReplacesExpiredDataWithTheStoppedCatalogState()
        {
            using (var catalog = Catalog())
            {
                Publish(catalog, 1, DateTime.UtcNow.AddMinutes(-1));
                Assert.That(catalog.Current.Live, Is.False);
                catalog.Stop();
                Assert.That(catalog.Current.Notice, Is.EqualTo("Steam catalog stopped"));
                Assert.That(catalog.Current.Live, Is.False);
                Assert.That(catalog.Current.Find(1).Name, Is.EqualTo("Synthetic app 1"));
            }
        }
    }
}
