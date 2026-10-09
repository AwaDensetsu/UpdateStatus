using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PlayniteUpdateStatus.UI
{
    internal static class ReplacementLaunchWait
    {
        public static bool Wait(Task<bool> removal, Dispatcher dispatcher, Func<bool> isCurrent)
        {
            Func<bool> finished = () => removal.IsCompleted || !isCurrent() || dispatcher.HasShutdownStarted;
            if (dispatcher.CheckAccess())
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval = TimeSpan.FromMilliseconds(25) };
                timer.Tick += (s,e) => { if (finished()) frame.Continue = false; };
                try { if (!finished()) { timer.Start(); Dispatcher.PushFrame(frame); } }
                finally { timer.Stop(); }
            }
            else while (!finished()) removal.Wait(50);
            return !dispatcher.HasShutdownStarted && isCurrent() && removal.IsCompleted && removal.Result;
        }
    }
}
