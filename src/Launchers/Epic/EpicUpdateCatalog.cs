using System;
using System.Threading;
using System.Threading.Tasks;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public sealed class EpicUpdateCatalog : IDisposable
    {
        private readonly object gate = new object();
        private CancellationTokenSource lifetime;
        private int interval;
        private string folder;
        private readonly Func<string, EpicEndpoint> discover;
        private readonly Func<EpicEndpoint, CancellationToken, Task<string>> read;
        private bool disposed;
        private readonly Action<string,bool> log;
        private EpicSnapshot current = Empty("Not connected");
        public EpicUpdateCatalog(Func<string, EpicEndpoint> discover = null,
            Func<EpicEndpoint, CancellationToken, Task<string>> read = null, Action<string,bool> log = null)
        {
            this.discover = discover ?? EpicLocalAccess.Discover;
            this.read = read ?? EpicCefClient.ReadAsync;
            this.log = log;
        }
        public EpicSnapshot Current => Volatile.Read(ref current);
        private static EpicSnapshot Empty(string status) =>
            new EpicSnapshot(DateTime.MinValue, new EpicProduct[0], false, status);
        public void Configure(bool enabled, int pollingInterval, string installationFolder = null)
        {
            lock (gate)
            {
                interval = Math.Max(500, Math.Min(2000, pollingInterval));
                if (disposed) return;
                string selected = string.IsNullOrWhiteSpace(installationFolder) ? null : installationFolder.Trim();
                bool changed = !string.Equals(folder, selected, StringComparison.OrdinalIgnoreCase);
                folder = selected;
                if (!enabled || changed)
                {
                    lifetime?.Cancel(); lifetime = null;
                    Volatile.Write(ref current, Empty(enabled ? "Waiting for Epic connection…" : "Epic monitoring disabled"));
                }
                if (!enabled) return;
                if (lifetime != null) return;
                var source = new CancellationTokenSource();
                lifetime = source;
                Task.Run(() => RunAsync(source, selected));
            }
        }
        private async Task RunAsync(CancellationTokenSource source, string selectedFolder)
        {
            var token = source.Token;
            var lastFailure = EpicConnectionFailure.None;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    EpicSnapshot next;
                    bool discovering = true;
                    Exception failureError = null;
                    try
                    {
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            timeout.CancelAfter(TimeSpan.FromSeconds(4));
                            var endpoint = discover(selectedFolder);
                            discovering = false;
                            next = endpoint == null ? Empty("Waiting for Epic connection…") :
                                EpicSnapshot.Parse(await read(endpoint, timeout.Token).ConfigureAwait(false), DateTime.UtcNow);
                        }
                    }
                    catch (Exception error) when (!(error is OutOfMemoryException))
                    {
                        if (token.IsCancellationRequested) break;
                        var failure = EpicConnectionFailures.Classify(error,discovering);
                        next = new EpicSnapshot(DateTime.MinValue,new EpicProduct[0],false,EpicConnectionFailures.Message(failure),failure);
                        failureError = error;
                    }
                    lock (gate)
                    {
                        if (disposed || lifetime != source || token.IsCancellationRequested) break;
                        Volatile.Write(ref current, next);
                    }
                    if (failureError != null && next.Failure != lastFailure)
                        ObserverNotifications.LogFailure(log,"Epic connection " + next.Failure,failureError);
                    lastFailure = next.Failure;
                    await Task.Delay(next.Connected ? Volatile.Read(ref interval) : 5000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { source.Dispose(); }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true; lifetime?.Cancel(); lifetime = null;
                Volatile.Write(ref current, Empty("Disconnected"));
            }
        }
    }
}
