using System;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.Launchers.Epic
{
    public sealed class EpicUpdateAdapter : ILauncherUpdateAdapter
    {
        private readonly EpicUpdateCatalog catalog;
        public string Name => "Epic Games";
        public EpicUpdateAdapter(EpicUpdateCatalog catalog) { this.catalog = catalog; }
        public IUpdateSession Open(LaunchIdentity game) => new EpicUpdateSession(game.LauncherId, () => catalog.Current);
    }

    // The launcher owns Play. This observer never launches or changes a download.
    public sealed class EpicUpdateSession : IUpdateSession, IUpdateDismissSession
    {
        private readonly string app;
        private readonly Func<EpicSnapshot> snapshot;
        private bool disposed;
        public EpicUpdateSession(string app, Func<EpicSnapshot> snapshot)
        { this.app = app; this.snapshot = snapshot; }
        public UpdateObservation Poll()
        {
            if (disposed) return new UpdateObservation { Phase = UpdatePhase.Idle };
            var current = snapshot();
            var product = current?.Find(app);
            if (product == null || !product.Initialized || product.Running == null)
                return Waiting("Waiting for Epic update data…");
            if (product.Running == true) return new UpdateObservation { Phase = UpdatePhase.Running };
            if (!product.Installed && !product.NeedsAttention)
                return Waiting("Waiting for Epic game data…");
            if (product.NeedsAttention)
                return new UpdateObservation {
                    Phase = UpdatePhase.Updating, ReceivedAtUtc = current.ReceivedUtc,
                    Display = new UpdateDisplay { Stage = product.Updating == true ? "Updating" :
                        product.Queued == true ? "Queued" : "Update required", WaitingForData = true,
                        Notice = "Manage this download in the launcher" }
                };
            if (product.Updating == null || product.Queued == null || product.HasUpdate == null)
                return Waiting("Waiting for Epic update data…");
            // Queue disappearance is not proof of completed work. Never emit Ready
            // or an invented 100%; the native launcher handles its Play request.
            return new UpdateObservation { Phase = UpdatePhase.Idle, ReceivedAtUtc = current.ReceivedUtc };
        }
        private static UpdateObservation Waiting(string message) => new UpdateObservation {
            Phase = UpdatePhase.WaitingForData, UserMessage = message
        };
        public void Dispose() { disposed = true; }
    }
}
