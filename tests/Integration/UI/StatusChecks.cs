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
    private static void UI()
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
            var text = new TextBlock
            {
                DataContext = new
                {
                    Original = "Original launch text",
                    OriginalVisibility = Visibility.Visible
                }
            };
            text.SetBinding(TextBlock.TextProperty, new Binding("Original"));
            text.SetBinding(UIElement.VisibilityProperty, new Binding("OriginalVisibility"));
            var root = new Grid();
            root.Children.Add(text);
            var window = new TestWindow
            {
                Width = 720,
                Height = 450,
                Content = root,
                Template = BareWindowTemplate()
            };
            TestTheme(window);
            try
            {
                window.Show();
                window.UpdateLayout();
                var state = new UpdateState
                {
                    Revision = 2,
                    GameId = Guid.NewGuid(),
                    Phase = UpdatePhase.Updating,
                    Display = DisplayFixture()
                };
                foreach (bool fullscreen in new[]
                {
                    false,
                    true
                }

                )
                {
                    Eq(true, presenter.Refresh(window, state, fullscreen));
                    window.UpdateLayout();
                    Eq(2, root.Children.Count);
                    var card = ((Canvas)root.Children[1]).Children.OfType<StatusControl>().Single();
                    var panel = (StackPanel)((Border)card.Content).Child;
                    Eq(state.Display.DownloadText, ((TextBlock)panel.Children[1]).Text);
                    Eq(62.0, ((ProgressBar)panel.Children[2]).Value);
                    Eq(93.0, ((ProgressBar)panel.Children[4]).Value);
                    Eq(window.Resources["FontMain"], ((TextBlock)panel.Children[1]).FontFamily);
                    Eq("Original launch text", text.Text);
                    Eq(Visibility.Visible, text.Visibility);
                    Eq(true, BindingOperations.IsDataBound(text, TextBlock.TextProperty));
                    Eq(true, BindingOperations.IsDataBound(text, UIElement.VisibilityProperty));
                    presenter.Dispose();
                    Eq(1, root.Children.Count);
                    // Automatic display can be re-enabled without duplicating the card.
                    Eq(true, presenter.Refresh(window, state, fullscreen));
                    presenter.Refresh(window, state, fullscreen);
                    Eq(2, root.Children.Count);
                    presenter.Refresh(window, new UpdateState { Phase = UpdatePhase.Idle }, fullscreen);
                    Eq(1, root.Children.Count);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void UniversalSurface()
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
            var state = new UpdateState
            {
                Revision = 10,
                GameId = Guid.NewGuid(),
                GameName = "Any Steam game",
                Phase = UpdatePhase.Updating,
                Display = DisplayFixture()
            };
            var root = new Grid
            {
                Name = "BrandNewThemeLayout",
                Background = System.Windows.Media.Brushes.DarkSlateGray
            };
            root.Children.Add(new Button { Content = "Theme button", Width = 180, Height = 40, VerticalAlignment = VerticalAlignment.Top });
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
            window.Show();
            window.UpdateLayout();
            Eq(true, presenter.Refresh(window, state, true));
            window.UpdateLayout();
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(root);
            var adorners = layer.GetAdorners(root);
            Eq(1, adorners.Length);
            Eq(true, adorners[0].IsHitTestVisible);
            Eq(false, adorners[0].Focusable);
            Eq(decorator, window.Content);
            Eq(1, root.Children.Count);
            var card = (StatusControl)System.Windows.Media.VisualTreeHelper.GetChild(adorners[0], 0);
            Eq(Visibility.Visible, card.Visibility);
            var header = ((StackPanel)((Border)card.Content).Child).Children.OfType<Grid>().Single();
            Eq("Any Steam game", header.Children.OfType<TextBlock>().Single().Text);
            presenter.Refresh(window, state, true);
            Eq(1, layer.GetAdorners(root).Length);
            Render(decorator, "universal-fullscreen.png");
            window.Width = 400;
            window.UpdateLayout();
            presenter.Refresh(window, state, false);
            window.UpdateLayout();
            Eq(true, card.ActualWidth <= root.ActualWidth - 39);
            Render(decorator, "universal-narrow.png");
            presenter.Refresh(window, new UpdateState { Revision = 11, Phase = UpdatePhase.Running }, true);
            Eq((System.Windows.Documents.Adorner[])null, layer.GetAdorners(root));
            Eq(decorator, window.Content);
            window.Close();
            var fallbackRoot = new Grid
            {
                Name = "UnrelatedDesktopTheme",
                Background = System.Windows.Media.Brushes.Black
            };
            var button = new Button
            {
                Content = "Existing button",
                Width = 180,
                Height = 40,
                VerticalAlignment = VerticalAlignment.Top
            };
            fallbackRoot.Children.Add(button);
            var fallbackWindow = new TestWindow
            {
                Width = 720,
                Height = 440,
                Content = fallbackRoot,
                ShowInTaskbar = false,
                ShowActivated = false,
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Window'><ContentPresenter Content='{TemplateBinding Content}'/></ControlTemplate>")
            };
            fallbackWindow.Show();
            fallbackWindow.UpdateLayout();
            Eq((System.Windows.Documents.AdornerLayer)null, System.Windows.Documents.AdornerLayer.GetAdornerLayer(fallbackRoot));
            Eq(true, presenter.Refresh(fallbackWindow, state, false));
            fallbackWindow.UpdateLayout();
            Eq(2, fallbackRoot.Children.Count);
            Eq(fallbackRoot, fallbackWindow.Content);
            var canvas = (Canvas)fallbackRoot.Children[1];
            Eq(true, canvas.IsHitTestVisible);
            Render(fallbackRoot, "universal-desktop.png");
            var replacement = new Grid
            {
                Background = System.Windows.Media.Brushes.DarkBlue
            };
            fallbackWindow.Content = replacement;
            fallbackWindow.UpdateLayout();
            presenter.Refresh(fallbackWindow, state, false);
            Eq(1, fallbackRoot.Children.Count);
            Eq(1, replacement.Children.Count);
            fallbackWindow.Close();
            Eq(0, replacement.Children.Count);
        }
    }

    private static void LifecycleUI()
    {
        if (Application.Current == null)
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var progress = new SteamProgress();
            var game = Guid.NewGuid();
            var updating = SteamLiveSample.Parse(LiveFrame(550, 1294, 50, 100, 70, 100), 550);
            var verifying = SteamLiveSample.Parse(LiveFrame(550, 1294, 100, 100, 100, 100), 550);
            verifying.UpdateState = 129;
            var installed = SteamLiveSample.Parse(LiveFrame(550, 4, 100, 100, 100, 100), 550);
            installed.UpdateState = 0;
            var session = new StepSession(new[] { LiveEval(progress, updating), LiveEval(progress, verifying), LiveEval(progress, installed), LiveEval(progress, installed), new UpdateObservation { Phase = UpdatePhase.Running } });
            var window = new TestWindow
            {
                Width = 680,
                Height = 420,
                ShowInTaskbar = false,
                ShowActivated = false,
                Template = BareWindowTemplate()
            };
            var root = new Grid();
            var text = new TextBlock
            {
                Text = "Original"
            };
            root.Children.Add(text);
            window.Content = root;
            using (var bridge = new UniversalStatusPresenter(monitor, (s, d) =>
            {
            }))
            {
                monitor.Changed += state => window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (state.Revision == monitor.Current.Revision)
                        bridge.Refresh(window, state, true);
                }));
                window.Show();
                monitor.Start(new LaunchIdentity { GameId = game }, session, 500);
                PumpUntil(() => monitor.Current.Display != null && root.Children.Count == 2);
                Eq(UpdatePhase.Updating, monitor.Current.Phase);
                session.Step = 1;
                PumpUntil(() => monitor.Current.Display != null && monitor.Current.Display.Stage == "Verifying files");
                Eq(UpdatePhase.Updating, monitor.Current.Phase);
                Eq((int?)100, monitor.Current.Percent);
                session.Step = 2;
                PumpUntil(() => monitor.Current.Display != null && monitor.Current.Display.Stage == "Finalizing");
                Eq(UpdatePhase.Updating, monitor.Current.Phase);
                session.Step = 3;
                PumpUntil(() => monitor.Current.Phase == UpdatePhase.Ready);
                Eq((int?)100, monitor.Current.Percent);
                session.Step = 4;
                PumpUntil(() => monitor.Current.Phase == UpdatePhase.Idle && root.Children.Count == 1);
                Eq(true, session.Disposed);
                Eq(Visibility.Visible, text.Visibility);
                Eq("Original", text.Text);
            }

            window.Close();
        }
    }
}
