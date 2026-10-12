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
    private sealed class ControlSession : IUpdateControlSession
    {
        public volatile bool Paused, Accept = true, Disposed;
        private int calls;
        public int Calls
        {
            get
            {
                return Interlocked.CompareExchange(ref calls, 0, 0);
            }
        }

        public ManualResetEvent Hold;
        public UpdateObservation Poll()
        {
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                CanPause = !Paused,
                CanResume = Paused,
                Display = new UpdateDisplay
                {
                    Stage = Paused ? "Paused" : "Updating files",
                    Paused = Paused,
                    Downloaded = 100,
                    DownloadTotal = 1000,
                    DownloadPercent = 10,
                    WorkPercent = 15
                }
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            Interlocked.Increment(ref calls);
            if (Hold != null)
                Hold.WaitOne(4000);
            return Accept;
        }

        public void Dispose()
        {
            Disposed = true;
        }
    }

    private static void ControlLifecycle()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new ControlSession();
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            Eq(false, monitor.RequestCommand(Guid.NewGuid(), UpdateCommand.Pause));
            Eq(false, monitor.RequestCommand(id, (UpdateCommand)99));
            monitor.ControlsEnabled = false;
            Eq(false, monitor.RequestCommand(id, UpdateCommand.Pause));
            monitor.ControlsEnabled = true;
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
            Until(() => session.Calls == 1);
            Eq(true, monitor.Current.CommandPending);
            Eq(false, monitor.Current.Display.Paused);
            Eq(false, monitor.RequestCommand(id, UpdateCommand.Pause));
            session.Paused = true;
            Until(() => !monitor.Current.CommandPending && monitor.Current.CanResume);
            Eq(true, monitor.Current.Display.Paused);
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
            Until(() => session.Calls == 2);
            Eq(true, monitor.Current.CommandPending);
            Eq(true, monitor.Current.Display.Paused);
            session.Paused = false;
            Until(() => !monitor.Current.CommandPending && monitor.Current.CanPause);
            session.Accept = false;
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
            Until(() => !monitor.Current.CommandPending && monitor.Current.CommandNotice != null);
            Eq(true, monitor.Current.CommandNotice.Contains("not accepted"));
            Eq(UpdatePhase.Updating, monitor.Current.Phase);
            Eq(false, monitor.Current.Display.Paused);
            monitor.Stop(id);
            Eq(false, monitor.RequestCommand(id, UpdateCommand.Pause));
            Eq(3, session.Calls);
        }
    }

    private sealed class GlobalSession : IUpdateControlSession
    {
        public volatile bool DownloadsPaused, AppPaused = true;
        public int Calls;
        public UpdateObservation Poll()
        {
            return new UpdateObservation
            {
                Phase = UpdatePhase.Updating,
                CanPause = !DownloadsPaused,
                CanResume = DownloadsPaused,
                ControlPaused = DownloadsPaused,
                ControlScope = "Steam downloads",
                Display = new UpdateDisplay
                {
                    Stage = "Updating files",
                    Paused = AppPaused
                }
            };
        }

        public bool SendCommand(UpdateCommand command)
        {
            Interlocked.Increment(ref Calls);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private static void GlobalControlState()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new GlobalSession();
            monitor.Start(new LaunchIdentity { GameId = id, Name = "Rust — global control fixture" }, session, 500);
            Until(() => monitor.Current.CanPause);
            Eq(true, monitor.Current.Display.Paused);
            Eq((bool?)false, monitor.Current.ControlPaused);
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
            Until(() => monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
            Thread.Sleep(600);
            Eq(true, monitor.Current.CommandPending); // Rust already paused is not global confirmation.
            session.AppPaused = false;
            session.DownloadsPaused = true;
            Until(() => !monitor.Current.CommandPending && monitor.Current.CanResume);
            Eq(false, monitor.Current.Display.Paused);
            Eq((bool?)true, monitor.Current.ControlPaused);
            Eq("Steam downloads", monitor.Current.ControlScope);
            var card = new StatusControl(monitor, true, true, true)
            {
                Width = 360
            };
            card.Render(monitor.Current);
            var button = ((StackPanel)((Border)card.Content).Child).Children.OfType<Button>().Single();
            Eq("Resume", button.Content);
            Eq((object)null, button.ToolTip);
            Eq(false, ToolTipService.GetIsEnabled(button));
            card.Measure(new Size(360, double.PositiveInfinity));
            card.Arrange(new Rect(0, 0, 360, card.DesiredSize.Height));
            card.UpdateLayout();
            Render(card, "steam-download-controls.png");
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Resume));
            Until(() => monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
            Thread.Sleep(600);
            Eq(true, monitor.Current.CommandPending);
            session.AppPaused = true;
            session.DownloadsPaused = false;
            Until(() => !monitor.Current.CommandPending && monitor.Current.CanPause);
            card.Render(monitor.Current);
            Eq("Pause", button.Content);
            Eq((object)null, button.ToolTip);
            Eq(false, ToolTipService.GetIsEnabled(button));
            Eq(true, monitor.Current.Display.Paused);
        }
    }

    private static void SpeedUnits()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Eq("1.0 MB/s", UpdateDisplay.FormatSpeed(1048576, SpeedUnit.BytesPerSecond));
            Eq("8.4 Mbps", UpdateDisplay.FormatSpeed(1048576, SpeedUnit.BitsPerSecond));
            Eq("488.3 KB/s", UpdateDisplay.FormatSpeed(500000, SpeedUnit.BytesPerSecond));
            Eq("4.0 Mbps", UpdateDisplay.FormatSpeed(500000, SpeedUnit.BitsPerSecond));
            Eq("4.0 kbps", UpdateDisplay.FormatSpeed(500, SpeedUnit.BitsPerSecond));
            Eq("0.0 KB/s", UpdateDisplay.FormatSpeed(0, SpeedUnit.BytesPerSecond));
            Eq(string.Empty, UpdateDisplay.FormatSpeed(double.NaN, SpeedUnit.BitsPerSecond));
            Eq(string.Empty, UpdateDisplay.FormatSpeed(double.PositiveInfinity, SpeedUnit.BytesPerSecond));
            Eq(string.Empty, UpdateDisplay.FormatSpeed(-1, SpeedUnit.BytesPerSecond));
            var display = new UpdateDisplay
            {
                BytesPerSecond = 500000,
                SecondsRemaining = 60
            };
            Eq("4.0 Mbps · Estimated 01:00 remaining", display.GetMetricsText(SpeedUnit.BitsPerSecond));
            display.Paused = true;
            Eq("Download paused", display.GetMetricsText(SpeedUnit.BitsPerSecond));
            display.Paused = false;
            Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("el-GR");
            Eq("4,0 Mbps", UpdateDisplay.FormatSpeed(500000, SpeedUnit.BitsPerSecond));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }

    private static void DismissStatus()
    {
        if (Application.Current == null)
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        using (var presenter = new UniversalStatusPresenter(monitor, (s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new ControlSession();
            monitor.Start(new LaunchIdentity { GameId = id, Name = "Rust — units and close fixture" }, session, 500);
            Until(() => monitor.Current.CanPause);
            var root = new Grid
            {
                Background = System.Windows.Media.Brushes.DarkSlateGray
            };
            var decorator = new System.Windows.Documents.AdornerDecorator
            {
                Child = root
            };
            var window = new TestWindow
            {
                Width = 900,
                Height = 500,
                Content = decorator,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            TestTheme(window);
            try
            {
                window.Show();
                window.UpdateLayout();
                presenter.Refresh(window, monitor.Current, true);
                window.UpdateLayout();
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(root);
                var card = (StatusControl)System.Windows.Media.VisualTreeHelper.GetChild(layer.GetAdorners(root)[0], 0);
                var panel = (StackPanel)((Border)card.Content).Child;
                var close = panel.Children.OfType<Grid>().Single().Children.OfType<Button>().Single();
                Eq("×", close.Content);
                Eq(true, close.IsEnabled);
                Eq(true, close.Focusable);
                Eq((object)null, close.ToolTip);
                Eq(false, ToolTipService.GetIsEnabled(close));
                Eq((Style)null, close.Style.BasedOn);
                var glyph = System.Windows.Media.VisualTreeHelper.GetChild(System.Windows.Media.VisualTreeHelper.GetChild(close, 0), 0) as System.Windows.Shapes.Path;
                Eq(true, glyph != null && glyph.IsVisible && glyph.ActualWidth >= 10 && glyph.ActualHeight >= 10);
                var currentId = monitor.Current.SessionId;
                Eq(false, monitor.SetStatusDismissed(Guid.NewGuid(), currentId, true));
                Render(decorator, "status-close.png");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => session.Calls == 1 && monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
                Eq(false, monitor.Current.Dismissed);
                Eq(true, monitor.Current.CommandPending);
                Eq(false, close.IsEnabled);
                Eq(false, monitor.RequestClose(id, currentId));
                Eq(1, session.Calls);
                session.Paused = true;
                Until(() => monitor.Current.Dismissed);
                card.Render(monitor.Current);
                Eq(Visibility.Collapsed, card.Visibility);
                Eq(false, presenter.Refresh(window, monitor.Current, true));
                Eq((System.Windows.Documents.Adorner[])null, layer.GetAdorners(root));
                Thread.Sleep(600);
                Eq(true, monitor.Current.Dismissed);
                Eq(UpdatePhase.Updating, monitor.Current.Phase);
                Eq(false, session.Disposed);
                Eq(1, session.Calls);
                Eq(true, monitor.SetStatusDismissed(id, currentId, false));
                Eq(true, presenter.Refresh(window, monitor.Current, true));
                Eq(true, monitor.RequestClose(id, currentId));
                Eq(true, monitor.Current.Dismissed);
                Eq(1, session.Calls);
                var next = new ControlSession();
                monitor.Start(new LaunchIdentity { GameId = id }, next, 500);
                Until(() => monitor.Current.CanPause);
                Eq(false, monitor.SetStatusDismissed(id, currentId, true));
                Eq(false, monitor.Current.Dismissed);
                Eq(false, monitor.RequestClose(id, currentId));
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Eq(0, next.Calls);
                Eq(false, monitor.Current.Dismissed);
                Eq(true, monitor.SetStatusDismissed(id, monitor.Current.SessionId, true));
                monitor.Stop(id);
                Eq(UpdatePhase.Idle, monitor.Current.Phase);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void CloseFailures()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        using (var hold = new ManualResetEvent(false))
        {
            var id = Guid.NewGuid();
            var session = new ControlSession
            {
                Accept = false
            };
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            long token = monitor.Current.SessionId;
            Eq(true, monitor.RequestClose(id, token));
            Until(() => session.Calls == 1 && !monitor.Current.CommandPending);
            Eq(false, monitor.Current.Dismissed);
            Eq(true, monitor.Current.CommandNotice.Contains("not accepted"));
            monitor.ControlsEnabled = false;
            Eq(false, monitor.RequestClose(id, token));
            Eq(false, monitor.Current.Dismissed);
            Eq(1, session.Calls);
            monitor.ControlsEnabled = true;
            session.Accept = true;
            session.Hold = hold;
            Eq(true, monitor.RequestClose(id, token));
            Until(() => session.Calls == 2);
            var replacement = new ControlSession();
            monitor.Start(new LaunchIdentity { GameId = id }, replacement, 500);
            Until(() => monitor.Current.CanPause);
            hold.Set();
            Thread.Sleep(100);
            Eq(false, monitor.Current.Dismissed);
            Eq(0, replacement.Calls);
            Eq(false, monitor.RequestClose(id, token));
            Eq(false, monitor.Current.Dismissed);
            monitor.Start(new LaunchIdentity { GameId = id }, new FixedSession(), 500);
            Until(() => monitor.Current.Phase == UpdatePhase.Updating);
            Eq(false, monitor.RequestClose(id, monitor.Current.SessionId));
            Eq(false, monitor.Current.Dismissed);
            Eq(true, monitor.Current.CommandNotice.Contains("unavailable"));
        }
    }

    private static void CloseGlobalState()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new GlobalSession();
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            long token = monitor.Current.SessionId;
            Eq(true, monitor.Current.Display.Paused);
            Eq(true, monitor.RequestClose(id, token));
            Until(() => monitor.Current.CommandNotice == "Waiting for launcher confirmation…");
            Thread.Sleep(600);
            Eq(false, monitor.Current.Dismissed);
            Eq(true, monitor.Current.CommandPending);
            Eq(1, session.Calls);
            session.AppPaused = false;
            session.DownloadsPaused = true;
            Until(() => monitor.Current.Dismissed);
            Eq(true, monitor.SetStatusDismissed(id, token, false));
            Eq(true, monitor.RequestClose(id, token));
            Eq(true, monitor.Current.Dismissed);
            Eq(1, session.Calls);
        }
    }

    private static void ControlCancellation()
    {
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        using (var hold = new ManualResetEvent(false))
        {
            var session = new ControlSession
            {
                Hold = hold
            };
            var id = Guid.NewGuid();
            monitor.Start(new LaunchIdentity { GameId = id }, session, 500);
            Until(() => monitor.Current.CanPause);
            Eq(true, monitor.RequestCommand(id, UpdateCommand.Pause));
            Until(() => session.Calls == 1);
            monitor.Stop(id);
            hold.Set();
            Thread.Sleep(100);
            Eq(true, session.Disposed);
            Eq(UpdatePhase.Idle, monitor.Current.Phase);
            Eq(false, monitor.Current.CommandPending);
            Eq((string)null, monitor.Current.CommandNotice);
        }
    }

    private static void ControlSurface()
    {
        if (Application.Current == null)
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        using (var presenter = new UniversalStatusPresenter(monitor, (s, d) =>
        {
        }))
        {
            var id = Guid.NewGuid();
            var session = new ControlSession();
            monitor.Start(new LaunchIdentity { GameId = id, Name = "Rust — control fixture" }, session, 500);
            Until(() => monitor.Current.CanPause);
            var root = new Grid
            {
                Background = System.Windows.Media.Brushes.DarkSlateGray
            };
            var themeButton = new Button
            {
                Content = "Existing theme button",
                Width = 180,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Top
            };
            root.Children.Add(themeButton);
            var decorator = new System.Windows.Documents.AdornerDecorator
            {
                Child = root
            };
            var window = new TestWindow
            {
                Width = 900,
                Height = 520,
                Content = decorator,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                presenter.Refresh(window, monitor.Current, true);
                window.UpdateLayout();
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(root);
                var adorner = layer.GetAdorners(root)[0];
                var card = (StatusControl)System.Windows.Media.VisualTreeHelper.GetChild(adorner, 0);
                var panel = (StackPanel)((Border)card.Content).Child;
                var button = panel.Children.OfType<Button>().Single();
                Eq("Pause", button.Content);
                Eq(true, button.IsEnabled);
                Eq(true, button.Focusable);
                Eq(true, adorner.IsHitTestVisible);
                Eq(false, adorner.Focusable);
                var point = themeButton.TranslatePoint(new Point(90, 20), decorator);
                var hit = decorator.InputHitTest(point) as DependencyObject;
                while (hit != null && hit != themeButton)
                    hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
                Eq(themeButton, hit); // Full-window adorner passes clicks outside the card.
                Render(decorator, "pause-resume-fullscreen.png");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Until(() => session.Calls == 1);
                card.Render(monitor.Current);
                Eq(false, button.IsEnabled);
                Eq("Please wait…", button.Content);
                session.Paused = true;
                Until(() => !monitor.Current.CommandPending && monitor.Current.CanResume);
                presenter.Refresh(window, monitor.Current, true);
                window.UpdateLayout();
                Eq("Resume", button.Content);
                Eq(true, button.IsEnabled);
                Render(decorator, "pause-resume-paused.png");
                monitor.ControlsEnabled = false;
                presenter.Refresh(window, monitor.Current, true);
                Eq(Visibility.Collapsed, button.Visibility);
                Eq(true, adorner.IsHitTestVisible);
                monitor.ControlsEnabled = true;
                var fallback = new Grid
                {
                    Background = System.Windows.Media.Brushes.DarkSlateGray
                };
                var fallbackButton = new Button
                {
                    Content = "Desktop theme button",
                    Width = 180,
                    Height = 40,
                    VerticalAlignment = VerticalAlignment.Top
                };
                fallback.Children.Add(fallbackButton);
                window.Content = fallback;
                window.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Window'><ContentPresenter Content='{TemplateBinding Content}'/></ControlTemplate>");
                window.UpdateLayout();
                presenter.Refresh(window, monitor.Current, false);
                window.UpdateLayout();
                var canvas = fallback.Children.OfType<Canvas>().Single();
                Eq(true, canvas.IsHitTestVisible);
                var fallbackCard = (StatusControl)canvas.Children[0];
                var fallbackAction = ((StackPanel)((Border)fallbackCard.Content).Child).Children.OfType<Button>().Single();
                Eq("Resume", fallbackAction.Content);
                var fallbackHit = fallback.InputHitTest(fallbackButton.TranslatePoint(new Point(90, 20), fallback)) as DependencyObject;
                while (fallbackHit != null && fallbackHit != fallbackButton)
                    fallbackHit = System.Windows.Media.VisualTreeHelper.GetParent(fallbackHit);
                Eq(fallbackButton, fallbackHit);
                Render(fallback, "pause-resume-desktop.png");
                monitor.Stop(id);
                presenter.Refresh(window, monitor.Current, true);
                Eq((System.Windows.Documents.Adorner[])null, layer.GetAdorners(root));
                Eq(1, fallback.Children.Count);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void ControlAbiRefusal()
    {
        string root = Path.Combine(Path.GetTempPath(), "UpdateStatusControl-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "SteamUI.dll");
        File.WriteAllText(path, "unverified frontend fixture");
        try
        {
            using (var control = new SteamUpdateControl(root, 252490, (s, d) =>
            {
            }))
            {
                Eq(false, control.Supported);
                Eq(false, control.Send(UpdateCommand.Pause));
            }

            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SteamUpdateReader.exe"), "\"" + root + "\" 252490 --ui-downloads-command pause")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (var child = System.Diagnostics.Process.Start(info))
            {
                if (!child.WaitForExit(5000))
                {
                    child.Kill();
                    throw new Exception("Command refusal timed out");
                }

                Eq(1, child.ExitCode);
                Eq(true, child.StandardError.ReadToEnd().Contains("Steam UI connection unavailable"));
            }

            info.Arguments = "\"" + root + "\" 252490 --command pause";
            using (var child = System.Diagnostics.Process.Start(info))
            {
                if (!child.WaitForExit(5000))
                {
                    child.Kill();
                    throw new Exception("Legacy command refusal timed out");
                }

                Eq(2, child.ExitCode);
            }

            foreach (string removed in new[]
            {
                "--start-update",
                "--check-start-update"
            }

            )
            {
                info.Arguments = "\"" + root + "\" 252490 " + removed;
                using (var child = System.Diagnostics.Process.Start(info))
                {
                    if (!child.WaitForExit(5000))
                    {
                        child.Kill();
                        throw new Exception("Removed start switch timed out");
                    }

                    Eq(2, child.ExitCode);
                }
            }
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    private static void CatalogLifecycle()
    {
        string session = "";
        int opens = 0;
        var apps = new List<SteamCatalogApp>
        {
            new SteamCatalogApp(550, "First"),
            new SteamCatalogApp(551, "Second")
        };
        var readers = new List<QueueFixture>();
        using (var catalog = new SteamUpdateCatalog(() => session, () => apps.ToList(), found =>
        {
            opens++;
            var reader = new QueueFixture
            {
                Sample = CatalogFrame(string.Join(",", found.Select(app => app.AppId + ":6:-1")), found.Select(app => app.AppId).ToArray())
            };
            readers.Add(reader);
            return reader;
        }, (s, d) =>
        {
        }))
        {
            catalog.Start();
            Until(() => catalog.Current.Entries.Count == 2);
            Eq(0, opens);
            Eq(false, catalog.Current.Live);
            session = "first-session";
            catalog.RefreshSoon();
            Until(() => catalog.Current.Live);
            Eq(1, opens);
            Eq((bool?)true, catalog.Current.Find(550).RequiresUpdate);
            readers.Last().Sample = CatalogFrame("550:4:-1,551:14:0", new uint[] { 550, 551 }, "1", "0", "1", 2);
            catalog.RefreshSoon();
            Until(() => catalog.Current.Find(550).Status == SteamCatalogStatus.UpToDate);
            Eq(SteamCatalogStatus.Queued, catalog.Current.Find(551).Status);
            readers.Last().Sample.ReceivedUtc = DateTime.UtcNow.AddSeconds(-6);
            catalog.RefreshSoon();
            Until(() => !catalog.Current.Live);
            Eq((bool?)null, catalog.Current.Find(550).RequiresUpdate);
            session = "replacement-session";
            catalog.RefreshSoon();
            Until(() => opens == 2);
            Eq(true, readers.First().Disposed);
            apps.Add(new SteamCatalogApp(552, "Added"));
            catalog.RefreshSoon();
            Until(() => catalog.Current.Entries.Count == 3 && opens == 3);
            Eq(true, readers[1].Disposed);
            session = "";
            catalog.RefreshSoon();
            Until(() => !catalog.Current.Live && catalog.Current.Notice == "Steam is closed");
            Eq(true, readers.Last().Disposed);
            Eq(3, opens);
            catalog.Stop();
            session = "another-session";
            catalog.RefreshSoon();
            Thread.Sleep(100);
            Eq(3, opens);
        }
    }
}
