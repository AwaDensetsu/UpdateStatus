using System;

namespace PlayniteUpdateStatus.Core
{
    // Open/Poll observe only. Optional controls are isolated in IUpdateControlSession
    // and require an explicit user action; they never launch games or edit files.
    public interface ILauncherUpdateAdapter
    {
        string Name { get; }
        IUpdateSession Open(LaunchIdentity game);
    }

    public interface IUpdateSession : IDisposable
    {
        UpdateObservation Poll();
    }
}
