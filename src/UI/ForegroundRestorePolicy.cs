using System;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    public enum ForegroundOwner { Unknown, Frontend, Launcher, Other }
    public sealed class ForegroundRestorePolicy
    {
        private bool active, relinquished;
        private int attempts;
        private DateTime expires, nextAttempt;
        public void Reset() { active = relinquished = false; attempts = 0; }
        public bool ShouldAttempt(UpdateState state, ForegroundOwner owner, bool gameRunning, DateTime now)
        {
            if (gameRunning || state.Phase != UpdatePhase.Updating) { Reset(); return false; }
            if (state.ControlPaused == true || state.Display != null && (state.Display.Paused || state.Display.WaitingForData)) return false;
            if (!active) { active = true; expires = now.AddSeconds(8); attempts = 0; relinquished = false; nextAttempt = now; }
            if (owner == ForegroundOwner.Other || owner == ForegroundOwner.Frontend && attempts > 0) relinquished = true;
            if (relinquished || owner != ForegroundOwner.Launcher || now > expires || attempts >= 3 || now < nextAttempt) return false;
            attempts++; nextAttempt = now.AddMilliseconds(1500); return true;
        }
        public void RecordResult(bool accepted) { if (accepted) relinquished = true; }
    }
}
