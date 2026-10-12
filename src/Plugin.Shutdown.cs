using System;
using System.Threading;

namespace PlayniteUpdateStatus
{
    public sealed partial class Plugin
    {
        private volatile bool closing;
        private int shutdownStarted;

        private void Shutdown()
        {
            if (Interlocked.Exchange(ref shutdownStarted, 1) != 0 || stopped) return;
            closing = true;
            launchCoordinator.Stop();
            try
            {
                // The worker uses launcher getters only, never the UI dispatcher.
                // Keep the reader/catalog alive until confirmation or a bounded
                // timeout; Dispose then cancels any outstanding helper process.
                var budget = TimeSpan.FromSeconds(9);
                var completion = Monitor.PrepareForExit(launchCoordinator.ActiveLauncher?.Registration.Key == "epic" ? Core.ExitDownloadAction.KeepCurrentState : Settings.ExitAction, budget);
                if (!completion.Wait(budget) || !completion.Result)
                    Log("Exit download action was not confirmed; no retry or later launch will be scheduled", true);
            }
            catch (Exception e) { Log("Exit download action unavailable: " + e.Message, true); }
            finally { Dispose(); }
        }
    }
}
