using System;

namespace PlayniteUpdateStatus.Core
{
    internal static class ObserverNotifications
    {
        internal static bool Notify(Delegate handlers, Action<Delegate> invoke, Action<string,bool> log, string name, bool failFast = false)
        {
            bool delivered = true;
            if (handlers == null) return true;
            foreach (var handler in handlers.GetInvocationList())
            {
                try { invoke(handler); }
                catch (Exception error) when (!(error is OutOfMemoryException))
                {
                    LogFailure(log,name,error);
                    delivered = false;
                    if (failFast) break;
                }
            }
            return delivered;
        }
        internal static void LogFailure(Action<string,bool> log, string name, Exception error)
        {
            // Do not copy exception messages or user paths into observer diagnostics.
            try { log?.Invoke(name + " failed: " + error.GetType().Name,true); }
            catch (Exception loggingError) when (!(loggingError is OutOfMemoryException)) { }
        }
    }
}
