using System;
using System.Threading;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        private volatile bool closing;
        private int shutdownStarted;

        private void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownStarted,1) != 0 || stopped) return;
            closing = true;
            lock (selectionGate)
            {
                ++launchRequest;
                if (steamRecovery != null) steamRecovery.Cancel();
                if (selectedLaunch != null) selectedLaunch.Cancel();
                if (pausedLaunch != null) pausedLaunch.ResumeConfirmed = false;
                pausedLaunch = queuedResume = null;
                deferredPlay = null;
            }
            try
            {
                // The worker uses launcher getters only, never the UI dispatcher.
                // Keep the reader/catalog alive until confirmation or a bounded
                // timeout; Dispose then cancels any outstanding helper process.
                var budget = TimeSpan.FromSeconds(9);
                var completion = Monitor.PrepareForExit(Settings.ExitAction,budget);
                if (!completion.Wait(budget) || !completion.Result)
                    Log("Exit download action was not confirmed; no retry or later launch will be scheduled",true);
            }
            catch (Exception e) { Log("Exit download action unavailable: " + e.Message,true); }
            finally { Dispose(); }
        }
    }
}
