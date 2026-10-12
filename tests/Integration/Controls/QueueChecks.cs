using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Reflection;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Remoting.Messaging;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;
using static UpdateStatus.Tests.Support.SteamSamples;

internal static partial class Program
{
    private sealed class QueueCloseSession : IUpdateCloseSession
    {
        public volatile bool Paused, Queued = true, Accept = true, Disposed, Complete;
        public int CloseCalls, PauseCalls, ResumeCalls;
        public ManualResetEvent AckRelease;
        public ManualResetEvent CommandEntered, CommandRelease;
        public UpdateObservation Poll()
        {
            var frame = CatalogFrame("550:" + (Queued ? "14:0" : "1542:-1"), new uint[] { 550 }, Paused ? "0" : "1", Queued && !Paused ? "550" : "0", Queued ? "1" : "0");
            return new UpdateObservation
            {
                Phase = Complete ? UpdatePhase.Ready : UpdatePhase.Updating,
                Display = new UpdateDisplay
                {
                    Paused = Paused
                },
                CanPause = !Paused,
                CanResume = Paused,
                ControlPaused = Paused,
                CloseConfirmed = SteamQueueClosePolicy.Confirmed(frame, 550)
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            if (CommandEntered != null)
                CommandEntered.Set();
            if (CommandRelease != null)
                CommandRelease.WaitOne(5000);
            if (command == UpdateCommand.Pause)
            {
                PauseCalls++;
                Paused = true;
                return true;
            }

            if (command == UpdateCommand.Resume)
            {
                ResumeCalls++;
                Paused = false;
                return true;
            }

            return false;
        }

        public bool SendClose()
        {
            Interlocked.Increment(ref CloseCalls);
            if (Accept)
                Paused = true;
            if (AckRelease != null)
                AckRelease.WaitOne(3000);
            return Accept;
        }

        public void Dispose()
        {
            Disposed = true;
            if (AckRelease != null)
                AckRelease.Set();
            if (CommandRelease != null)
                CommandRelease.Set();
        }
    }

    private static void ReplacementRemoval()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession
            {
                Paused = true
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanResume);
            int closes = 0;
            monitor.CloseRequested += (g, t) => closes++;
            var removal = monitor.RemoveForReplacement(id, monitor.Current.SessionId);
            Eq(removal, monitor.RemoveForReplacement(id, monitor.Current.SessionId));
            Until(() => session.CloseCalls == 1);
            Eq(false, removal.IsCompleted);
            Eq(false, monitor.RequestCommand(id, UpdateCommand.Resume));
            Eq(false, monitor.RequestClose(id, monitor.Current.SessionId));
            session.Queued = false;
            Until(() => removal.IsCompleted);
            Eq(true, removal.Result);
            Until(() => !monitor.Current.CommandPending);
            Eq(false, monitor.Current.Dismissed);
            Eq(0, closes);
            Eq(1, session.CloseCalls);
        }

        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession
            {
                Paused = true,
                CommandEntered = entered,
                CommandRelease = release
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanResume);
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
            Eq(true, entered.WaitOne(1000));
            var removal = monitor.RemoveForReplacement(id, monitor.Current.SessionId);
            Thread.Sleep(75);
            Eq(0, session.CloseCalls);
            release.Set();
            Until(() => session.CloseCalls == 1);
            Eq(true, session.Paused);
            session.Queued = false;
            Until(() => removal.IsCompleted);
            Eq(true, removal.Result);
            Eq(1, session.ResumeCalls);
        }
    }

    private static void ReplacementRemovalFailure()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        using (var release = new ManualResetEvent(false))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession
            {
                Accept = false
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            var removal = monitor.RemoveForReplacement(id, monitor.Current.SessionId);
            Until(() => removal.IsCompleted);
            Eq(false, removal.Result);
            Eq(false, monitor.Current.Dismissed);
            session.Accept = true;
            session.AckRelease = release;
            var retry = monitor.RemoveForReplacement(id, monitor.Current.SessionId);
            Until(() => session.CloseCalls == 2);
            monitor.Stop(id);
            Until(() => retry.IsCompleted);
            Eq(false, retry.Result);
            Eq(true, session.Disposed);
            Eq(UpdatePhase.Idle, monitor.Current.Phase);
        }
    }

    private static void QueueAwareClose()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession
            {
                Paused = true
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanResume);
            Eq(true, monitor.RequestClose(id, monitor.Current.SessionId));
            Until(() => session.CloseCalls == 1 && monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
            Thread.Sleep(600);
            Eq(false, monitor.Current.Dismissed);
            Eq(true, monitor.Current.CommandPending); // global Pause alone is insufficient
            Eq(false, monitor.RequestClose(id, monitor.Current.SessionId));
            Eq(1, session.CloseCalls);
            session.Queued = false;
            Until(() => monitor.Current.Dismissed);
            Eq(true, session.Paused);
            Eq(0, session.PauseCalls);
            Eq(0, session.ResumeCalls);
        }
    }

    private static void QueueAwarePauseResume()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession();
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            Eq(false, monitor.RequestCommand(id, UpdateCommand.Close));
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
            Until(() => monitor.Current.CanResume && !monitor.Current.CommandPending);
            Eq(true, session.Queued);
            Eq(0, session.CloseCalls);
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
            Until(() => monitor.Current.CanPause && !monitor.Current.CommandPending);
            Eq(true, session.Queued);
            Eq(0, session.CloseCalls);
            Eq(1, session.PauseCalls);
            Eq(1, session.ResumeCalls);
        }
    }

    private static void QueueRemovalEvidence()
    {
        Eq(true, SteamSelectionPolicy.CanClose(12));
        Eq(true, SteamSelectionPolicy.CanClose(14));
        Eq(true, SteamSelectionPolicy.CanClose(1542));
        Eq(false, SteamSelectionPolicy.CanClose(8196));
        Eq(false, SteamSelectionPolicy.CanClose(4 | 2048));
        Eq(false, SteamSelectionPolicy.CanClose(4 | 4096));
        Eq(false, SteamSelectionPolicy.CanClose(0x80000006));
        Eq(false, SteamSelectionPolicy.CanClose(0));
        var frame = CatalogFrame("550:1542:-1", new uint[] { 550 }, "0");
        Eq((bool?)true, SteamQueueClosePolicy.Confirmed(frame, 550));
        Eq((bool?)null, SteamQueueClosePolicy.Confirmed(frame, 551));
        Eq((bool?)null, SteamQueueClosePolicy.Confirmed(frame, 550, frame.ReceivedUtc));
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(-6);
        Eq((bool?)null, SteamQueueClosePolicy.Confirmed(frame, 550));
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(1);
        Eq((bool?)null, SteamQueueClosePolicy.Confirmed(frame, 550));
        Eq((bool?)false, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:1542:-1", new uint[] { 550 }), 550)); // globals resumed
        Eq((bool?)false, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:1542:0", new uint[] { 550 }, "0", "0", "1"), 550)); // paused but still queued
        Eq((bool?)false, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:14:-1", new uint[] { 550 }, "0"), 550)); // queued flag races with index
        Eq((bool?)false, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:1542:-1", new uint[] { 550 }, "0", "550"), 550));
        Eq((bool?)null, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:-:-", new uint[] { 550 }, "0"), 550));
        Eq((bool?)null, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:1542:-1", new uint[] { 550 }, "-", "-", "-"), 550));
        Eq((bool?)false, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:8196:-1", new uint[] { 550 }, "0"), 550));
        Eq((bool?)true, SteamQueueClosePolicy.Confirmed(CatalogFrame("550:4:-1,551:14:0", new uint[] { 550, 551 }, "0", "0", "1"), 550)); // other app retained
    }

    private static void QueueCloseCompletionRace()
    {
        using (var release = new ManualResetEvent(false))
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession
            {
                AckRelease = release
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            Eq(true, monitor.RequestClose(id, monitor.Current.SessionId));
            Until(() => session.CloseCalls == 1);
            session.Complete = true;
            session.Queued = false;
            Until(() => monitor.Current.Phase == UpdatePhase.Ready);
            Eq(true, monitor.Current.CommandPending);
            Eq(false, monitor.Current.Dismissed);
            release.Set();
            Until(() => monitor.Current.Dismissed);
            Eq(false, monitor.Current.CommandPending);
            Eq(1, session.CloseCalls);
        }
    }

    private static void QueueCloseRefusal()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new QueueCloseSession
            {
                Accept = false,
                Paused = true
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanResume);
            Eq(true, monitor.RequestClose(id, monitor.Current.SessionId));
            Until(() => session.CloseCalls == 1 && !monitor.Current.CommandPending);
            Eq(false, monitor.Current.Dismissed);
            Eq(true, monitor.Current.CommandNotice.Contains("not accepted"));
            Eq(true, session.Queued);
            monitor.ControlsEnabled = false;
            Eq(false, monitor.RequestClose(id, monitor.Current.SessionId));
            Eq(1, session.CloseCalls);
            monitor.ControlsEnabled = true;
            session.Accept = true;
            Eq(true, monitor.RequestClose(id, monitor.Current.SessionId));
            Until(() => session.CloseCalls == 2);
            session.Queued = false;
            Until(() => monitor.Current.Dismissed);
            Eq(0, session.ResumeCalls);
        }
    }

    private static void QueueCloseReplacement()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        using (var release = new ManualResetEvent(false))
        {
            var oldId = Guid.NewGuid();
            var old = new QueueCloseSession
            {
                AckRelease = release
            };
            monitor.Start(new LaunchIdentity { GameId = oldId }, old, 500);
            Until(() => monitor.Current.CanPause);
            long oldSession = monitor.Current.SessionId;
            Eq(true, monitor.RequestClose(oldId, oldSession));
            Until(() => old.CloseCalls == 1);
            var id = Guid.NewGuid();
            var next = new QueueCloseSession();
            monitor.Start(new LaunchIdentity { GameId = id }, next, 500);
            release.Set();
            Until(() => monitor.Current.CanPause);
            Eq(true, old.Disposed);
            Eq(id, monitor.Current.GameId);
            Eq(false, monitor.Current.Dismissed);
            Eq(0, next.CloseCalls);
            Eq(false, monitor.RequestClose(oldId, oldSession));
            Eq(0, next.CloseCalls);
        }
    }

    private sealed class QueueFixture : ISteamQueueReader
    {
        public SteamQueueSample Sample;
        public bool Disposed;
        public string Notice
        {
            get
            {
                return null;
            }
        }

        public SteamQueueSample TryRead()
        {
            return Sample;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}
