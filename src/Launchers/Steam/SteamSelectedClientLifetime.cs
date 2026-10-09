using System;

namespace PlayniteUpdateStatus.Launchers.Steam
{
    // Steam's bootstrap process may hand over before IPC is ready. Pin the client
    // only after a real reply, then refuse exit/restart for this retained Play.
    public sealed class SteamSelectedClientLifetime
    {
        private string connectedSession;
        public bool Observe(string session, bool freshReply)
        {
            if (connectedSession != null && session != connectedSession)
                throw new InvalidOperationException("Steam exited or restarted; selected Play must be requested again");
            if (string.IsNullOrEmpty(session)) return false;
            if (freshReply && connectedSession == null) connectedSession = session;
            return true;
        }
    }
}
