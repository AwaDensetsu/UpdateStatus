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
    private sealed class SelectionFixture : IDisposable
    {
        public readonly uint AppId;
        public uint Flags = 1030, Stage;
        public volatile bool Enabled, Running, Complete, Freeze, WrongApp;
        public bool SavedError, HidePausedCounters;
        public int Prepares, Selections, Disposals, Commands;
        public int Removals;
        public bool Removed;
        public bool RemovalAccept = true;
        public volatile bool RemovalConfirmed = true;
        public Action OnSelection;
        public ManualResetEvent RemovalEntered, RemovalRelease;
        public SteamSelectionResult Result = SteamSelectionResult.Accepted;
        public readonly SteamSelectedUpdateSession Session;
        public readonly ResumeSession Observer = new ResumeSession();
        public SteamLiveSample Last;
        public SelectionFixture(uint id)
        {
            AppId = id;
            Session = new SteamSelectedUpdateSession(id, Observer, Read, () =>
            {
                if (OnSelection != null)
                    OnSelection();
                Selections++;
                if (Result == SteamSelectionResult.Accepted)
                {
                    Enabled = true;
                    Flags = 1294;
                }

                return Result;
            }, () => Prepares++, () => Disposals++, (s, d) =>
            {
            }, command =>
            {
                Commands++;
                Enabled = command == UpdateCommand.Resume;
                return true;
            }, () =>
            {
                Commands++;
                Removals++;
                if (RemovalEntered != null)
                    RemovalEntered.Set();
                if (RemovalRelease != null)
                    RemovalRelease.WaitOne(5000);
                if (!RemovalAccept)
                    return false;
                Enabled = false;
                Observer.Paused = true;
                Removed = true;
                return true;
            }, () => Removed && !Enabled && RemovalConfirmed);
        }

        public SteamLiveSample Read()
        {
            Observer.Phase = SavedError ? UpdatePhase.Error : UpdatePhase.Updating;
            Observer.Paused = !Enabled;
            Observer.WaitingForData = HidePausedCounters && !Enabled;
            if (Freeze && Last != null)
                return Last;
            // Production replies arrive once a second. Distinct fixture replies
            // need distinct receipts even on Windows' coarse system clock.
            Thread.Sleep(20);
            return Last = new SteamLiveSample
            {
                AppId = WrongApp ? AppId + 1 : AppId,
                InstallState = Running ? 8196u : Complete ? 4u : HidePausedCounters && !Enabled ? 14u : Flags,
                UpdateState = Stage,
                Result = SavedError ? 6u : 0u,
                HasCounters = !Observer.WaitingForData,
                DownloadsEnabled = Enabled,
                ReceivedAtUtc = DateTime.UtcNow
            };
        }

        public void Dispose()
        {
            Session.Dispose();
        }
    }

    private static void SelectedSessionLifecycle()
    {
        using (var f = new SelectionFixture(550))
        {
            f.SavedError = true;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(1, f.Prepares);
            Eq(1, f.Selections);
            Eq(true, f.Enabled);
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(1, f.Selections); // saved error is not a cancellation
            f.SavedError = false;
            Eq(true, f.Session.Poll().CanPause);
            Eq(true, f.Session.SendCommand(UpdateCommand.Pause));
            Eq(true, f.Session.Poll().CanResume);
            f.Complete = true;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(1, f.Selections);
            Eq(false, f.Enabled);
            Eq(true, f.Session.SendCommand(UpdateCommand.Resume));
            Eq(UpdatePhase.Ready, f.Session.Poll().Phase);
            Eq(1, f.Prepares);
            Eq(1, f.Selections);
            Eq(2, f.Commands);
            f.Session.Dispose();
            Eq(1, f.Disposals);
            Eq(UpdatePhase.Idle, f.Session.Poll().Phase);
            Eq(false, f.Session.SendCommand(UpdateCommand.Resume));
        }
    }

    private static void SelectedCompletionEvidence()
    {
        using (var f = new SelectionFixture(550))
        {
            f.Session.Poll();
            f.Complete = true;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            f.Freeze = true;
            for (int i = 0; i < 3; i++)
                Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            f.Last.ReceivedAtUtc = DateTime.UtcNow.AddSeconds(-6);
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            f.Freeze = false;
            f.WrongApp = true;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            f.WrongApp = false;
            f.Stage = 1;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            f.Stage = 0;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(UpdatePhase.Ready, f.Session.Poll().Phase);
            f.Running = true;
            Eq(UpdatePhase.Running, f.Session.Poll().Phase);
        }

        Eq(false, SteamSelectionPolicy.Pending(8198));
        Eq(false, SteamSelectionPolicy.Pending(0x80000006));
        Eq(false, SteamSelectionPolicy.Complete(12));
        Eq(false, SteamSelectionPolicy.Complete(4 | 512));
        Eq(true, SteamSelectionPolicy.Pending(1030));
        Eq(true, SteamSelectionPolicy.Complete(4));
    }

    private static void SelectedPreAcknowledgementCompletion()
    {
        var before = DateTime.UtcNow.AddMilliseconds(-200);
        var sample = new SteamLiveSample
        {
            AppId = 550,
            InstallState = 6,
            DownloadsEnabled = true,
            ReceivedAtUtc = before
        };
        using (var session = new SteamSelectedUpdateSession(550, new ResumeSession(), () => sample, () => SteamSelectionResult.Accepted, () =>
        {
        }, () =>
        {
        }, (s, d) =>
        {
        }))
        {
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            sample = new SteamLiveSample
            {
                AppId = 550,
                InstallState = 4,
                DownloadsEnabled = true,
                ReceivedAtUtc = before.AddMilliseconds(20)
            };
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            sample = new SteamLiveSample
            {
                AppId = 550,
                InstallState = 4,
                DownloadsEnabled = true,
                ReceivedAtUtc = before.AddMilliseconds(40)
            };
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            Thread.Sleep(20);
            sample.ReceivedAtUtc = DateTime.UtcNow;
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            Thread.Sleep(20);
            sample.ReceivedAtUtc = DateTime.UtcNow;
            Eq(UpdatePhase.Ready, session.Poll().Phase);
        }
    }

    private static void SelectedPauseException()
    {
        bool complete = false;
        int commands = 0;
        using (var session = new SteamSelectedUpdateSession(550, new ResumeSession(), () =>
        {
            Thread.Sleep(20);
            return new SteamLiveSample
            {
                AppId = 550,
                InstallState = complete ? 4u : 6u,
                DownloadsEnabled = true,
                ReceivedAtUtc = DateTime.UtcNow
            };
        }, () => SteamSelectionResult.Accepted, () =>
        {
        }, () =>
        {
        }, (s, d) =>
        {
        }, command =>
        {
            commands++;
            throw new IOException("Command pipe interrupted before acknowledgement");
        }))
        {
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            bool refused = false;
            try
            {
                session.SendCommand(UpdateCommand.Pause);
            }
            catch (IOException)
            {
                refused = true;
            }

            Eq(true, refused);
            Eq(1, commands);
            complete = true;
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            Eq(UpdatePhase.Ready, session.Poll().Phase);
        }
    }

    private static void CatalogFreshness()
    {
        var apps = new[]
        {
            new SteamCatalogApp(550, "fixture")
        };
        var sample = new SteamLiveSample
        {
            AppId = 550,
            InstallState = 4,
            ReceivedAtUtc = DateTime.UtcNow
        };
        foreach (var receipt in new[]
        {
            DateTime.UtcNow.AddSeconds(-6),
            DateTime.UtcNow.AddSeconds(10),
            DateTime.MinValue
        }

        )
        {
            var frame = CatalogFrame("550:4:-1", new uint[] { 550 });
            frame.ReceivedUtc = receipt;
            var snapshot = new SteamCatalogSnapshot(apps, frame, null);
            Eq(false, snapshot.Fresh);
            Eq(sample, SteamSelectedUpdateSession.WithCatalogGlobals(sample, snapshot));
        }
    }

    private static void SelectedRefusal()
    {
        using (var f = new SelectionFixture(550))
        {
            f.Result = SteamSelectionResult.Rejected;
            Eq(UpdatePhase.Error, f.Session.Poll().Phase);
            f.Complete = true;
            f.Enabled = true;
            Eq(UpdatePhase.Error, f.Session.Poll().Phase);
            Eq(1, f.Selections);
        }

        using (var f = new SelectionFixture(550))
        {
            f.Result = SteamSelectionResult.Waiting;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(1, f.Selections);
            f.Result = SteamSelectionResult.Accepted;
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(2, f.Selections); // first dry consistency refusal sent no command
            Eq(UpdatePhase.Updating, f.Session.Poll().Phase);
            Eq(2, f.Selections);
        }
    }

    private static void SelectedHelperGuards()
    {
        string helper = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamUpdateReader.exe");
        string root = Path.Combine(Path.GetTempPath(), "playnite-selection-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "SteamUI.dll"), "unsupported");
            Eq(false, SteamUpdateControl.SupportsSelection(root));
            using (var sender = new SteamUpdateControl(root, 550, (s, d) =>
            {
            }))
            {
                Eq(SteamSelectionResult.Rejected, sender.Select());
                Eq(false, sender.SendSelected(UpdateCommand.Pause));
            }

            foreach (string arguments in new[]
            {
                "550 --ui-select-update",
                "550 --ui-check-select-update",
                "550 --ui-selected-downloads-command pause",
                "550 --ui-close-update",
                "550 --ui-check-close-update",
                "0 --ui-select-update",
                "550 --ui-select-update extra"
            }

            )
            {
                var info = new System.Diagnostics.ProcessStartInfo(helper, "\"" + root + "\" " + arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using (var process = System.Diagnostics.Process.Start(info))
                {
                    string stderr = process.StandardError.ReadToEnd();
                    process.StandardOutput.ReadToEnd();
                    Eq(true, process.WaitForExit(5000));
                    Eq(arguments.StartsWith("0") || arguments.EndsWith("extra") ? 2 : 1, process.ExitCode);
                    if (process.ExitCode == 1)
                        Eq(true, stderr.Contains("Steam UI connection unavailable"));
                }
            }
        }
        finally
        {
            File.Delete(Path.Combine(root, "SteamUI.dll"));
            Directory.Delete(root);
        }
    }

    private static void DisabledClosePreservesSelectedPlay()
    {
        using (var monitor = new UpdateMonitor((s, d) => { }))
        using (var returned = new ManualResetEvent(false))
        {
            var game = SelectedGame(550);
            var args = Event<OnGameStartingEventArgs>(game);
            var session = new ResumeSession();
            var state = monitor.Start(new LaunchIdentity { GameId = game.Id }, session, 500);
            var gate = new SelectedLaunchGate(args, state.SessionId);
            monitor.CloseRequested += (id, token) => gate.Cancel();
            bool released = false;
            var uiDispatcher = Dispatcher.CurrentDispatcher;
            ThreadPool.QueueUserWorkItem(_ => {
                try { released = gate.Wait(monitor, uiDispatcher); }
                finally { returned.Set(); }
            });
            try
            {
                Until(() => monitor.Current.CanPause);
                monitor.ControlsEnabled = false;
                Eq(false, monitor.RequestClose(game.Id, state.SessionId));
                Eq(false, args.CancelStartup);
                Eq(false, monitor.Current.Dismissed);
                session.Phase = UpdatePhase.Ready;
                Until(() => monitor.Current.Phase == UpdatePhase.Ready);
                Eq(true, returned.WaitOne(1500));
                Eq(true, released);
                Eq(false, args.CancelStartup);
            }
            finally
            {
                monitor.Stop(game.Id);
                Eq(true, returned.WaitOne(1500));
            }
        }
    }

    private static void SelectedLateAcknowledgement()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var returned = new ManualResetEvent(false))
        {
            int requests = 0, cleanup = 0;
            var sample = new SteamLiveSample
            {
                AppId = 550,
                InstallState = 6,
                DownloadsEnabled = true,
                HasCounters = true,
                ReceivedAtUtc = DateTime.UtcNow
            };
            using (var monitor = new UpdateMonitor((s, d) =>
            {
            }))
            {
                var session = new SteamSelectedUpdateSession(550, new ResumeSession(), () => sample, () =>
                {
                    requests++;
                    entered.Set();
                    release.WaitOne(2000);
                    return SteamSelectionResult.Accepted;
                }, () =>
                {
                }, () =>
                {
                    cleanup++;
                    release.Set();
                }, (s, d) =>
                {
                });
                var game = SelectedGame(550);
                var args = Event<OnGameStartingEventArgs>(game);
                var start = monitor.Start(new LaunchIdentity { GameId = game.Id }, session, 500);
                var gate = new SelectedLaunchGate(args, start.SessionId);
                var uiDispatcher = Dispatcher.CurrentDispatcher;
                bool released = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        released = gate.Wait(monitor, uiDispatcher);
                    }
                    finally
                    {
                        returned.Set();
                    }
                });
                Eq(true, entered.WaitOne(1000));
                gate.Cancel();
                var nextId = Guid.NewGuid();
                monitor.Start(new LaunchIdentity { GameId = nextId }, new ResumeSession(), 500);
                Eq(true, returned.WaitOne(1500));
                Eq(false, released);
                Eq(true, args.CancelStartup);
                Eq(1, requests);
                Eq(1, cleanup);
                sample.InstallState = 4;
                sample.ReceivedAtUtc = DateTime.UtcNow;
                Thread.Sleep(100);
                Eq(nextId, monitor.Current.GameId);
                Eq(UpdatePhase.Idle, session.Poll().Phase);
            }
        }
    }

    private static void SelectedCatalogGlobals()
    {
        var sample = new SteamLiveSample
        {
            AppId = 550,
            InstallState = 4,
            ReceivedAtUtc = DateTime.UtcNow
        };
        var apps = new[]
        {
            new SteamCatalogApp(550, "fixture")
        };
        var snapshot = new SteamCatalogSnapshot(apps, CatalogFrame("550:4:-1", new uint[] { 550 }), null);
        var merged = SteamSelectedUpdateSession.WithCatalogGlobals(sample, snapshot);
        Eq((bool?)true, merged.DownloadsEnabled);
        Eq(false, merged.HasCounters);
        Eq((uint)550, merged.AppId);
        Eq((bool?)null, sample.DownloadsEnabled);
        var mismatch = new SteamCatalogSnapshot(apps, CatalogFrame("550:6:-1", new uint[] { 550 }), null);
        Eq(sample, SteamSelectedUpdateSession.WithCatalogGlobals(sample, mismatch));
        snapshot = new SteamCatalogSnapshot(apps, null, "missing");
        Eq(sample, SteamSelectedUpdateSession.WithCatalogGlobals(sample, snapshot));
        var frame = CatalogFrame("550:4:-1", new uint[] { 550 });
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(-6);
        Eq(sample, SteamSelectedUpdateSession.WithCatalogGlobals(sample, new SteamCatalogSnapshot(apps, frame, null)));
        using (var session = new SteamSelectedUpdateSession(550, new ResumeSession(), () =>
        {
            sample.ReceivedAtUtc = DateTime.UtcNow;
            return sample;
        }, () =>
        {
            throw new Exception("No request is needed for an already completed game");
        }, () =>
        {
        }, () =>
        {
        }, (s, d) =>
        {
        }))
        {
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            Thread.Sleep(20);
            Eq(UpdatePhase.Updating, session.Poll().Phase);
        }
    }

    private static void SelectedClientExit()
    {
        bool exited = true;
        int reads = 0;
        using (var session = new SteamSelectedUpdateSession(550, new ResumeSession(), () =>
        {
            reads++;
            if (exited)
                throw new InvalidOperationException("Steam exited");
            return new SteamLiveSample
            {
                AppId = 550,
                InstallState = 4,
                DownloadsEnabled = true,
                ReceivedAtUtc = DateTime.UtcNow
            };
        }, () => SteamSelectionResult.Accepted, () =>
        {
        }, () =>
        {
        }, (s, d) =>
        {
        }))
        {
            Eq(UpdatePhase.Error, session.Poll().Phase);
            exited = false;
            Eq(UpdatePhase.Error, session.Poll().Phase);
            Eq(1, reads);
        }
    }

    private static void SelectedColdBootstrap()
    {
        var lifetime = new SteamSelectedClientLifetime();
        Eq(false, lifetime.Observe("", false));
        Eq(true, lifetime.Observe("bootstrap", false));
        Eq(false, lifetime.Observe("", false));
        Eq(true, lifetime.Observe("steam", false));
        Eq(true, lifetime.Observe("steam", true));
        Eq(true, lifetime.Observe("steam", true));
        foreach (string next in new[]
        {
            "",
            "bootstrap",
            "new-steam"
        }

        )
        {
            bool refused = false;
            try
            {
                lifetime.Observe(next, true);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }

            Eq(true, refused);
        }
    }

    private static void SelectionCloseOrdering()
    {
        using (var entered = new ManualResetEvent(false))
        using (var release = new ManualResetEvent(false))
        using (var polled = new ManualResetEvent(false))
        using (var closed = new ManualResetEvent(false))
        {
            int sequence = 0, selectedOrder = 0, closeOrder = 0, selections = 0, prepares = 0;
            var observer = new ResumeSession();
            using (var session = new SteamSelectedUpdateSession(550, observer, () => new SteamLiveSample { AppId = 550, InstallState = 6, DownloadsEnabled = true, ReceivedAtUtc = DateTime.UtcNow }, () =>
            {
                selections++;
                entered.Set();
                release.WaitOne(2000);
                selectedOrder = ++sequence;
                return SteamSelectionResult.Accepted;
            }, () => prepares++, () => release.Set(), (s, d) =>
            {
            }, null, () =>
            {
                closeOrder = ++sequence;
                observer.Paused = true;
                return true;
            }, () => closeOrder > 0))
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        session.Poll();
                    }
                    finally
                    {
                        polled.Set();
                    }
                });
                Eq(true, entered.WaitOne(1000));
                bool accepted = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        accepted = session.SendClose();
                    }
                    finally
                    {
                        closed.Set();
                    }
                });
                Eq(false, closed.WaitOne(100));
                release.Set();
                Eq(true, closed.WaitOne(1500));
                Eq(true, polled.WaitOne(1500));
                Eq(true, accepted);
                Eq(1, selectedOrder);
                Eq(2, closeOrder);
                Eq((bool?)true, session.Poll().CloseConfirmed);
                Eq(1, selections);
                Eq(1, prepares);
            }
        }
    }

    private static void SelectionCloseBeforePoll()
    {
        int selections = 0, prepares = 0, closes = 0;
        using (var session = new SteamSelectedUpdateSession(550, new ResumeSession(), () =>
        {
            throw new Exception("Closed selection cannot read/start its startup path");
        }, () =>
        {
            selections++;
            return SteamSelectionResult.Accepted;
        }, () => prepares++, () =>
        {
        }, (s, d) =>
        {
        }, null, () =>
        {
            closes++;
            return true;
        }, () => true))
        {
            Eq(true, session.SendClose());
            Eq((bool?)true, session.Poll().CloseConfirmed);
            Eq(UpdatePhase.Updating, session.Poll().Phase);
            Eq(0, selections);
            Eq(0, prepares);
            Eq(1, closes);
        }
    }
}
