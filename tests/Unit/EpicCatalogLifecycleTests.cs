using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PlayniteUpdateStatus.Launchers.Epic;

namespace UpdateStatus.Tests.Unit
{
    [TestFixture]
    public sealed class EpicCatalogLifecycleTests
    {
        private static string Response(string app) => "{\"products\":[{\"app\":\"" + app + "\",\"namespace\":\"ns\",\"catalog\":\"catalog\",\"initialized\":true,\"installed\":true,\"hasUpdate\":false,\"updating\":false,\"queued\":false,\"running\":false}]}";
        private static async Task Signal(Task task)
        {
            Assert.That(await Task.WhenAny(task, Task.Delay(2000)), Is.SameAs(task), "Catalog worker did not reach its test boundary");
            await task;
        }
        private static async Task WaitForProduct(EpicUpdateCatalog catalog, string app)
        {
            var until = DateTime.UtcNow.AddSeconds(2);
            while (catalog.Current.Find(app) == null && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.That(catalog.Current.Find(app), Is.Not.Null, catalog.Current.Status);
        }
        [TestCase(@"D:\Selected Epic", @"D:\Selected Epic")]
        [TestCase(@"  D:\Selected Epic  ", @"D:\Selected Epic")]
        [TestCase("", null)]
        public async Task DiscoveryUsesTheSelectedFolderOrExplicitAutomaticSelection(string input, string expected)
        {
            string requested = "not called";
            var called = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var catalog = new EpicUpdateCatalog(folder => { requested = folder; called.TrySetResult(true); return null; },
                (endpoint, token) => throw new AssertionException("No endpoint was authorized")))
            {
                catalog.Configure(true, 500, input);
                await Signal(called.Task);
                Assert.That(requested, Is.EqualTo(expected));
                Assert.That(catalog.Current.Fresh, Is.False);
            }
        }
        [TestCase(false)]
        [TestCase(true)]
        public async Task SwitchingInstallationsInvalidatesDataAndRejectsAnOldReplyIncludingAnABARoundTrip(bool roundTrip)
        {
            int requests = 0;
            var firstEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var second = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var folders = new List<string>();
            using (var catalog = new EpicUpdateCatalog(folder => {
                lock (folders) folders.Add(folder);
                return new EpicEndpoint(Interlocked.Increment(ref requests), 1);
            }, async (endpoint, token) => {
                if (endpoint.Port == 1)
                {
                    firstEntered.TrySetResult(true);
                    string data = await first.Task;
                    firstReturned.TrySetResult(true);
                    return data;
                }
                if (endpoint.Port == 2)
                {
                    secondEntered.TrySetResult(true);
                    return await second.Task;
                }
                return Response("latest");
            }))
            {
                try
                {
                    catalog.Configure(true, 500, "installation-A");
                    await Signal(firstEntered.Task);
                    catalog.Configure(true, 500, "installation-B");
                    await Signal(secondEntered.Task);
                    Assert.That(catalog.Current.Fresh, Is.False);
                    if (roundTrip) catalog.Configure(true, 500, "installation-A");
                    else second.SetResult(Response("latest"));
                    await WaitForProduct(catalog, "latest");
                    var received = catalog.Current.ReceivedUtc;
                    first.SetResult(Response("obsolete"));
                    await Signal(firstReturned.Task);
                    if (roundTrip) second.SetResult(Response("obsolete-B"));
                    await Task.Delay(100);
                    Assert.That(catalog.Current.Find("latest"), Is.Not.Null);
                    Assert.That(catalog.Current.Find("obsolete"), Is.Null);
                    Assert.That(catalog.Current.Find("obsolete-B"), Is.Null);
                    Assert.That(catalog.Current.ReceivedUtc, Is.GreaterThanOrEqualTo(received));
                    lock (folders)
                    {
                        Assert.That(folders[0], Is.EqualTo("installation-A"));
                        Assert.That(folders[1], Is.EqualTo("installation-B"));
                        if (roundTrip) Assert.That(folders[2], Is.EqualTo("installation-A"));
                    }
                }
                finally { first.TrySetResult(Response("obsolete")); second.TrySetResult(Response("obsolete-B")); }
            }
        }
        [Test]
        public async Task DisablingObservationClearsDataAndRejectsALateSuccessfulRead()
        {
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var returned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var catalog = new EpicUpdateCatalog(folder => new EpicEndpoint(1,1), async (endpoint, token) => {
                entered.TrySetResult(true);
                var data = await pending.Task;
                returned.TrySetResult(true);
                return data;
            }))
            {
                try
                {
                    catalog.Configure(true,500,"selected");
                    await Signal(entered.Task);
                    catalog.Configure(false,500,"selected");
                    pending.SetResult(Response("obsolete"));
                    await Signal(returned.Task);
                    await Task.Delay(100);
                    Assert.That(catalog.Current.Fresh, Is.False);
                    Assert.That(catalog.Current.Status, Is.EqualTo("Epic monitoring disabled"));
                }
                finally { pending.TrySetResult(Response("obsolete")); }
            }
        }
    }
}
