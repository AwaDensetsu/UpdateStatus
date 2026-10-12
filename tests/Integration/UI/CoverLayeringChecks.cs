using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

internal static class CoverLayeringChecks
{
    public static void MultipleSdkControls()
    {
        var first = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = SteamGameIdentity.LibraryId,
            GameId = "550"
        };
        var second = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = SteamGameIdentity.LibraryId,
            GameId = "551"
        };
        var sample = SteamQueueSample.Parse("Q3|1|0|0|1|550:6:-1,551:6:-1|-", new uint[] { 550, 551 });
        Func<SteamCatalogSnapshot> catalog = () =>
        {
            sample.ReceivedUtc = DateTime.UtcNow;
            return new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture"), new SteamCatalogApp(551, "fixture") }, sample, null);
        };
        var root = new Grid
        {
            Width = 600,
            Height = 450
        };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var art = new DrawingImage(new GeometryDrawing(Brushes.SlateGray, null, new RectangleGeometry(new Rect(0, 0, 300, 450))));
        art.Freeze();
        var coverFirst = new Image
        {
            Name = "PART_ImageCover",
            Source = art,
            DataContext = first
        };
        var coverSecond = new Image
        {
            Name = "PART_ImageCover",
            Source = art,
            DataContext = second
        };
        Grid.SetColumn(coverSecond, 1);
        var sdkFirst = new CoverBadgeControl(catalog, () => true)
        {
            GameContext = first
        };
        var sdkSecond = new CoverBadgeControl(catalog, () => true)
        {
            GameContext = second
        };
        Grid.SetColumn(sdkSecond, 1);
        root.Children.Add(coverFirst);
        root.Children.Add(coverSecond);
        root.Children.Add(sdkFirst);
        root.Children.Add(sdkSecond);
        var window = new Window
        {
            Content = root,
            Width = 640,
            Height = 500,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        using (var presenter = new CoverBadgePresenter((s, d) =>
        {
        }))
        {
            try
            {
                window.Show();
                window.UpdateLayout();
                sdkFirst.Refresh();
                sdkSecond.Refresh();
                // The first cover visited belongs to the second widget. Refresh
                // of the first widget must reorder safely before enumeration
                // continues to the matching widget in this shared panel.
                root.Children.Remove(coverSecond);
                root.Children.Insert(0, coverSecond);
                root.Children.Remove(sdkFirst);
                root.Children.Insert(1, sdkFirst);
                presenter.Refresh(window, catalog(), true);
                window.UpdateLayout();
                if (presenter.ActiveCount != 0 || sdkFirst.Visibility != Visibility.Visible || sdkSecond.Visibility != Visibility.Visible)
                    throw new Exception("Reordering SDK widgets lost duplicate suppression or visibility.");
                if (Grid.GetColumn(sdkFirst) != 0 || Grid.GetColumn(sdkSecond) != 1 || sdkFirst.GameContext.Id != first.Id || sdkSecond.GameContext.Id != second.Id)
                    throw new Exception("SDK reordering changed theme placement or game context.");
            }
            finally
            {
                sdkFirst.Shutdown();
                sdkSecond.Shutdown();
                window.Close();
            }
        }
    }

    public static void Run(bool sdkMode, bool wrapped)
    {
        var game = new Game
        {
            Id = Guid.NewGuid(),
            PluginId = SteamGameIdentity.LibraryId,
            GameId = "550",
            Name = "Layering fixture"
        };
        var art = new DrawingImage(new GeometryDrawing(Brushes.SlateGray, null, new RectangleGeometry(new Rect(0, 0, 300, 450))));
        art.Freeze();
        var image = new Image
        {
            Name = "PART_ImageCover",
            Source = art,
            Stretch = Stretch.Fill,
            DataContext = game
        };
        FrameworkElement branch = wrapped ? (FrameworkElement)new Border
        {
            Child = image
        }

        : image;
        branch.Tag = 0;
        branch.SetBinding(Panel.ZIndexProperty, new Binding("Tag") { Source = branch });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = 0
        };
        actions.SetBinding(Panel.ZIndexProperty, new Binding("Tag") { Source = actions });
        var play = Button("Play");
        var info = Button("Info");
        actions.Children.Add(play);
        actions.Children.Add(info);
        var root = new Grid
        {
            Width = 300,
            Height = 450,
            Background = Brushes.Black
        };
        root.Children.Add(branch);
        root.Children.Add(actions);
        var window = new Window
        {
            Content = root,
            Width = 340,
            Height = 500,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        var sample = SteamQueueSample.Parse("Q3|1|0|0|1|550:6:-1|-", new uint[] { 550 });
        Func<SteamCatalogSnapshot> catalog = () =>
        {
            sample.ReceivedUtc = DateTime.UtcNow;
            return new SteamCatalogSnapshot(new[] { new SteamCatalogApp(550, "fixture") }, sample, null);
        };
        bool enabled = false;
        UpdateState selected = null;
        var sdk = new CoverBadgeControl(catalog, () => enabled, null, () => selected)
        {
            GameContext = game
        };
        if (sdkMode)
            root.Children.Add(sdk);
        var coverBinding = BindingOperations.GetBindingBase(branch, Panel.ZIndexProperty);
        var actionBinding = BindingOperations.GetBindingBase(actions, Panel.ZIndexProperty);
        int clicks = 0;
        play.Click += (s, e) => clicks++;
        info.Click += (s, e) => clicks++;
        using (var presenter = new CoverBadgePresenter((s, d) =>
        {
        }))
        {
            Action refresh = () =>
            {
                if (sdkMode)
                    sdk.Refresh();
                else
                    presenter.Refresh(window, catalog(), enabled, null, selected);
                window.UpdateLayout();
            };
            try
            {
                window.Show();
                window.UpdateLayout();
                var expectedPlay = ButtonPixels(root, play);
                var expectedInfo = ButtonPixels(root, info);
                foreach (int z in new[]
                {
                    0,
                    -7,
                    12,
                    int.MinValue,
                    int.MaxValue
                }

                )
                {
                    branch.Tag = z;
                    actions.Tag = z;
                    foreach (int state in new[]
                    {
                        0,
                        1,
                        2,
                        3
                    }

                    )
                    {
                        selected = state == 0 ? null : new UpdateState
                        {
                            GameId = game.Id,
                            Phase = UpdatePhase.Updating,
                            ControlPaused = state == 3,
                            Display = new UpdateDisplay
                            {
                                WorkPercent = 93,
                                Downloaded = state == 1 ? 62u : 100u,
                                DownloadTotal = 100
                            }
                        };
                        enabled = true;
                        refresh();
                        if (sdkMode ? sdk.Visibility != Visibility.Visible : presenter.ActiveCount != 1)
                            throw new Exception("Fixture did not show its cover indicator.");
                        if (!expectedPlay.SequenceEqual(ButtonPixels(root, play)) || !expectedInfo.SequenceEqual(ButtonPixels(root, info)))
                        {
                            throw new Exception("Update artwork or shade paints over Play/Info (Z=" + z + ", state=" + state + ").");
                        }

                        AssertButtonHit(root, play);
                        AssertButtonHit(root, info);
                    }
                }

                // A higher action Z-index and a newly inserted hover panel must
                // remain above the badge, without editing their bindings/order.
                actions.Tag = branch.Tag = 12;
                root.Children.Remove(actions);
                root.Children.Insert(root.Children.IndexOf(branch) + 1, actions);
                refresh();
                if (!expectedPlay.SequenceEqual(ButtonPixels(root, play)))
                    throw new Exception("New hover actions are covered.");
                actions.Tag = 20;
                refresh();
                if (coverBinding != BindingOperations.GetBindingBase(branch, Panel.ZIndexProperty) || actionBinding != BindingOperations.GetBindingBase(actions, Panel.ZIndexProperty))
                    throw new Exception("Theme Z-index bindings were replaced.");
                if (Panel.GetZIndex(branch) != 12 || Panel.GetZIndex(actions) != 20 || image.Source != art || root.Children.IndexOf(branch) >= root.Children.IndexOf(actions))
                    throw new Exception("Theme layers or image source were altered.");
                actions.Visibility = Visibility.Collapsed;
                refresh();
                if (ButtonPixels(root, play).SequenceEqual(expectedPlay))
                    throw new Exception("Hidden actions still paint above the cover.");
                actions.Visibility = Visibility.Visible;
                refresh();
                AssertButtonHit(root, play);
                play.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                info.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                if (clicks != 2)
                    throw new Exception("Theme action clicks were intercepted.");
                selected = null;
                branch.Tag = actions.Tag = 0;
                refresh();
                Save(root, "cover-actions-" + (sdkMode ? "sdk" : "automatic") + (wrapped ? "-wrapped" : "-direct") + "-1.19.5.png");
                enabled = false;
                refresh();
                if (sdkMode ? sdk.Visibility != Visibility.Collapsed : presenter.ActiveCount != 0)
                    throw new Exception("Disabled indicator remained visible.");
                if (!expectedPlay.SequenceEqual(ButtonPixels(root, play)))
                    throw new Exception("Disabling changed the original action appearance.");
            }
            finally
            {
                sdk.Shutdown();
                presenter.Dispose();
                window.Close();
            }
        }
    }

    private static Button Button(string label)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.DarkSlateBlue);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return new Button
        {
            Content = label,
            Width = 120,
            Height = 70,
            Margin = new Thickness(3),
            Foreground = Brushes.White,
            FontSize = 22,
            Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = border
            }
        };
    }

    private static RenderTargetBitmap Capture(FrameworkElement root)
    {
        root.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
        }));
        // Draw the view at its own origin and capture the full composition.
        // Direct Render(root) can omit cached native text after Z-order changes.
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(root), null, new Rect(root.RenderSize));
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        return bitmap;
    }

    private static byte[] ButtonPixels(FrameworkElement root, Button button)
    {
        var point = button.TranslatePoint(new Point(), root);
        var rect = new Int32Rect((int)point.X + 3, (int)point.Y + 3, (int)button.ActualWidth - 6, (int)button.ActualHeight - 6);
        var pixels = new byte[rect.Width * rect.Height * 4];
        Capture(root).CopyPixels(rect, pixels, rect.Width * 4, 0);
        return pixels;
    }

    private static void AssertButtonHit(FrameworkElement root, Button button)
    {
        var hit = root.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), root)) as DependencyObject;
        while (hit != null && hit != button)
            hit = VisualTreeHelper.GetParent(hit);
        if (hit != button)
            throw new Exception("Play/Info hit testing no longer reaches the original button.");
    }

    private static void Save(FrameworkElement root, string name)
    {
        string directory = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "artifacts", "validation"));
        Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Capture(root)));
        using (var output = File.Create(Path.Combine(directory, name)))
            encoder.Save(output);
    }
}
