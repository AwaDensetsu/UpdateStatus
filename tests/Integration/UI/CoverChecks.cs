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
    public sealed class CoverContextFixture
    {
        public object Game { get; set; }
        public System.Windows.Media.ImageSource CoverImageObject { get; set; }
    }

    private static SteamCatalogSnapshot CoverCatalog(string rows = "550:6:-1,551:1294:0,552:1542:-1", string active = "551", string count = "1")
    {
        var apps = new[]
        {
            new SteamCatalogApp(550, "Requires fixture"),
            new SteamCatalogApp(551, "Updating fixture"),
            new SteamCatalogApp(552, "Paused fixture")
        };
        return new SteamCatalogSnapshot(apps, CatalogFrame(rows, new uint[] { 550, 551, 552 }, "1", active, count), null);
    }

    private static Game CoverGame(uint id)
    {
        return new Game
        {
            Id = Guid.NewGuid(),
            PluginId = SteamGameIdentity.LibraryId,
            GameId = id.ToString(),
            Name = "Cover fixture " + id,
            CoverImage = "original-cover-" + id + ".png",
            IsInstalled = true
        };
    }

    private static void CoverBadgeStates()
    {
        var catalog = CoverCatalog();
        var snapshot = new CoverBadgeSnapshot(catalog);
        var game = CoverGame(550);
        Eq("Update Required", snapshot.Find(game).Text);
        Eq(game.Id, snapshot.Find(game).GameId);
        Eq("Updating", snapshot.Find(CoverGame(551)).Text);
        Eq("Update Required", snapshot.Find(CoverGame(552)).Text);
        game.PluginId = Guid.NewGuid();
        Eq((CoverBadgeInfo)null, snapshot.Find(game));
        game.PluginId = SteamGameIdentity.LibraryId;
        game.GameId = "not-an-app";
        Eq((CoverBadgeInfo)null, snapshot.Find(game));
        game.GameId = "550";
        game.IsRunning = true;
        Eq((CoverBadgeInfo)null, snapshot.Find(game));
        game.IsRunning = false;
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(CoverCatalog("550:4:-1,551:20:-1,552:-:-", "0", "0")).Find(game));
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(CoverCatalog("550:4:-1,551:20:-1,552:-:-", "0", "0")).Find(CoverGame(551)));
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(CoverCatalog("550:4:-1,551:20:-1,552:-:-", "0", "0")).Find(CoverGame(552)));
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(CoverCatalog("550:8198:-1,551:4:-1,552:4:-1", "0", "0")).Find(game));
        var apps = new[]
        {
            new SteamCatalogApp(550, "fixture")
        };
        var frame = CatalogFrame("550:6:-1", new uint[] { 550 });
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(-6);
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(new SteamCatalogSnapshot(apps, frame, null)).Find(game));
        frame.ReceivedUtc = DateTime.UtcNow.AddSeconds(1);
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(new SteamCatalogSnapshot(apps, frame, null)).Find(game));
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(new SteamCatalogSnapshot(apps, null, "Steam closed")).Find(game));
        var selected = new UpdateState
        {
            GameId = game.Id,
            Phase = UpdatePhase.Updating,
            ControlPaused = false,
            Display = new UpdateDisplay
            {
                WorkPercent = 65,
                DownloadPercent = 20
            }
        };
        var live = new CoverBadgeSnapshot(catalog, null, selected).Find(game);
        Eq("Updating", live.Text);
        Eq((int?)65, live.Percent);
        Eq("Update Required", new CoverBadgeSnapshot(catalog, null, selected).Find(CoverGame(552)).Text);
        Eq((int?)null, new CoverBadgeSnapshot(catalog, null, selected).Find(CoverGame(551)).Percent);
        selected.ControlPaused = true;
        live = new CoverBadgeSnapshot(catalog, null, selected).Find(game);
        Eq("Paused", live.Text);
        Eq((int?)null, live.Percent);
        selected.ControlPaused = false;
        selected.Display.Paused = true;
        Eq("Updating", new CoverBadgeSnapshot(catalog, null, selected).Find(game).Text);
        selected.Display.WaitingForData = true;
        Eq((int?)null, new CoverBadgeSnapshot(catalog, null, selected).Find(game).Percent);
        selected.Display.WaitingForData = false;
        selected.Display.WorkPercent = 101;
        Eq((int?)null, new CoverBadgeSnapshot(catalog, null, selected).Find(game).Percent);
        selected.Display.WorkPercent = 100;
        Eq((int?)100, new CoverBadgeSnapshot(catalog, null, selected).Find(game).Percent);
        selected.Dismissed = true;
        Eq("Update Required", new CoverBadgeSnapshot(catalog, null, selected).Find(game).Text);
        selected.Dismissed = false;
        selected.Phase = UpdatePhase.Ready;
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(catalog, null, selected).Find(game));
        selected.Phase = UpdatePhase.Updating;
        Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(CoverCatalog("550:8198:-1,551:4:-1,552:4:-1", "1", "0"), null, selected).Find(game));
        var inner = new CoverContextFixture
        {
            Game = game
        };
        var outer = new CoverContextFixture
        {
            Game = inner
        };
        Eq(game, CoverGameContext.Resolve(outer));
        outer.Game = outer;
        Eq((Game)null, CoverGameContext.Resolve(outer));
    }

    private static System.Windows.Media.ImageSource CoverArt(byte r, byte g, byte b)
    {
        var group = new System.Windows.Media.DrawingGroup();
        group.Children.Add(new System.Windows.Media.GeometryDrawing(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b)), null, new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 200, 300))));
        group.Children.Add(new System.Windows.Media.GeometryDrawing(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 255, 255, 255)), null, new System.Windows.Media.EllipseGeometry(new Point(150, 230), 100, 110)));
        var art = new System.Windows.Media.DrawingImage(group);
        art.Freeze();
        return art;
    }

    private static Grid CoverTile(Game game, out Image image)
    {
        var grid = new Grid
        {
            Width = 150,
            Height = 225,
            Margin = new Thickness(10),
            DataContext = new CoverContextFixture
            {
                Game = game,
                CoverImageObject = CoverArt(35, 55, 80)
            }
        };
        image = new Image
        {
            Name = "PART_ImageCover",
            Stretch = System.Windows.Media.Stretch.Fill
        };
        image.SetBinding(Image.SourceProperty, new Binding("CoverImageObject"));
        grid.Children.Add(image);
        grid.Children.Add(new Button { Content = "Play", Width = 65, Height = 28, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) });
        return grid;
    }

    private static void CoverDownloadStages()
    {
        Eq("Updating", new CoverBadgeInfo(Guid.NewGuid(), null).CoverText);
        var game = CoverGame(550);
        var catalog = CoverCatalog();
        var display = new UpdateDisplay
        {
            Downloaded = 310,
            DownloadTotal = 500,
            DownloadPercent = 90,
            WorkPercent = 93,
            Stage = "Updating files"
        };
        var state = new UpdateState
        {
            GameId = game.Id,
            Phase = UpdatePhase.Updating,
            ControlPaused = false,
            Display = display,
            Percent = 93
        };
        Func<CoverBadgeInfo> badge = () => new CoverBadgeSnapshot(catalog, null, state).Find(game);
        Eq("Downloading", badge().CoverText);
        Eq((int?)62, badge().CoverPercent);
        Eq("Updating", badge().Text);
        Eq((int?)93, badge().Percent); // legacy values do not change the shared library presentation
        Eq("Updating files — 93%", display.StageText);
        Eq((int?)90, display.DownloadPercent); // no shared display mutation
        var other = new CoverBadgeSnapshot(catalog, null, state).Find(CoverGame(551));
        Eq("Updating", other.CoverText);
        Eq((int?)null, other.CoverPercent);
        state.ControlPaused = true;
        Eq("Paused", badge().CoverText);
        Eq((int?)null, badge().CoverPercent);
        state.ControlPaused = false;
        Eq((int?)62, badge().CoverPercent);
        display.Downloaded = 499;
        Eq((int?)99, badge().CoverPercent);
        display.DownloadTotal = 1000;
        Eq((int?)49, badge().CoverPercent); // revised totals can change percentage
        display.Downloaded = 0;
        Eq((int?)0, badge().CoverPercent);
        display.Downloaded = ulong.MaxValue - 1;
        display.DownloadTotal = ulong.MaxValue;
        Eq((int?)99, badge().CoverPercent);
        display.Downloaded = display.DownloadTotal;
        foreach (string stage in new[]
        {
            "Updating files",
            "Installing files",
            "Patching files",
            "Verifying files",
            "Unpacking files",
            "Validating files",
            "Reserving space",
            "Starting download",
            "Finalizing",
            "Preloading",
            "Stopping",
            "Queued"
        }

        )
        {
            display.Stage = stage;
            Eq(stage, badge().CoverText);
            Eq((int?)null, badge().CoverPercent);
        }

        display.Stage = null;
        Eq("Updating", badge().CoverText);
        display.Stage = "unknown provider stage";
        Eq("Updating", badge().CoverText);
        display.Stage = "Verifying files";
        display.Downloaded = null;
        Eq("Verifying files", badge().CoverText);
        Eq((int?)null, badge().CoverPercent);
        display.Downloaded = 101;
        display.DownloadTotal = 100;
        Eq((int?)null, badge().CoverPercent);
        display.Downloaded = 0;
        display.DownloadTotal = 0;
        Eq("Verifying files", badge().CoverText);
        display.Downloaded = 62;
        display.DownloadTotal = 100;
        display.WaitingForData = true;
        Eq("Updating", badge().CoverText);
        Eq((int?)null, badge().CoverPercent);
        display.WaitingForData = false;
        display.Paused = true;
        state.ControlPaused = null;
        Eq("Paused", badge().CoverText);
        state.ControlPaused = false;
        Eq("Downloading", badge().CoverText);
        state.Display = null;
        Eq("Updating", badge().CoverText);
        Eq((int?)null, badge().CoverPercent); // never reuse aggregate Percent
        state.Display = display;
        state.Phase = UpdatePhase.Ready;
        Eq((CoverBadgeInfo)null, badge());
        state.Phase = UpdatePhase.Running;
        Eq((CoverBadgeInfo)null, badge());
        state.Phase = UpdatePhase.Updating;
        state.Dismissed = true;
        Eq("Update Required", badge().CoverText);
    }

    private static void CoverDownloadRendering()
    {
        var game = CoverGame(550);
        var display = new UpdateDisplay
        {
            Downloaded = 62,
            DownloadTotal = 100,
            WorkPercent = 93,
            Stage = "Updating files"
        };
        Func<CoverBadgeInfo> info = () => new CoverBadgeInfo(game.Id, "Updating", 93, display);
        var reused = new CoverBadgeVisual();
        var fresh = new CoverBadgeVisual();
        var root = new Grid
        {
            Width = 300,
            Height = 450,
            Background = System.Windows.Media.Brushes.DarkSlateBlue
        };
        var window = new TestWindow
        {
            Content = root,
            Width = 340,
            Height = 500
        };
        Func<CoverBadgeVisual, byte[]> pixels = visual =>
        {
            root.Children.Clear();
            root.Children.Add(visual);
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
            }));
            var local = new System.Windows.Media.DrawingVisual();
            using (var dc = local.RenderOpen())
                dc.DrawRectangle(new System.Windows.Media.VisualBrush(root), null, new Rect(root.RenderSize));
            var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(local);
            byte[] data = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(data, bitmap.PixelWidth * 4, 0);
            return data;
        };
        try
        {
            window.Show();
            foreach (var size in new[]
            {
                new Size(300, 450),
                new Size(160, 160),
                new Size(320, 180)
            }

            )
            {
                root.Width = size.Width;
                root.Height = size.Height;
                display.Downloaded = 62;
                display.Stage = "Updating files";
                reused.Render(info());
                var downloadPixels = pixels(reused);
                Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName(reused));
                display.Downloaded = 80;
                reused.Render(info());
                fresh.Render(info());
                var nextPixels = pixels(reused);
                Eq(true, nextPixels.SequenceEqual(pixels(fresh)));
                Eq(false, nextPixels.SequenceEqual(downloadPixels));
                display.Downloaded = 100;
                reused.Render(info());
                fresh.Render(info());
                var stagePixels = pixels(reused);
                Eq("Updating files", System.Windows.Automation.AutomationProperties.GetName(reused));
                Eq(true, stagePixels.SequenceEqual(pixels(fresh)));
                Eq(false, stagePixels.SequenceEqual(nextPixels));
                display.Stage = "Verifying files";
                reused.Render(info());
                fresh.Render(info());
                var verifyPixels = pixels(reused);
                Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(reused));
                Eq(true, verifyPixels.SequenceEqual(pixels(fresh)));
                Eq(false, verifyPixels.SequenceEqual(stagePixels));
                reused.Render(new CoverBadgeInfo(game.Id, "Paused"));
                Eq("Paused", System.Windows.Automation.AutomationProperties.GetName(reused));
                reused.Render(null);
                Eq(Visibility.Collapsed, reused.Visibility);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static void CoverBadgeSurface()
    {
        if (Application.Current == null)
            new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
        var root = new Grid
        {
            Background = System.Windows.Media.Brushes.DarkSlateGray
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        root.Children.Add(row);
        var images = new List<Image>();
        var games = new List<Game>();
        foreach (uint id in new uint[]
        {
            550,
            551,
            552
        }

        )
        {
            Image image;
            var game = CoverGame(id);
            games.Add(game);
            row.Children.Add(CoverTile(game, out image));
            images.Add(image);
        }

        var window = new TestWindow
        {
            Width = 680,
            Height = 370,
            Content = root,
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
                var source = images[0].Source;
                var binding = BindingOperations.GetBindingBase(images[0], Image.SourceProperty);
                var parent = (Grid)row.Children[0];
                var button = parent.Children.OfType<Button>().Single();
                int clicks = 0;
                button.Click += (s, e) => clicks++;
                var selected = new UpdateState
                {
                    GameId = games[1].Id,
                    Phase = UpdatePhase.Updating,
                    ControlPaused = false,
                    Display = new UpdateDisplay
                    {
                        WorkPercent = 93,
                        Downloaded = 62,
                        DownloadTotal = 100
                    }
                };
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq(3, presenter.ActiveCount);
                for (int i = 0; i < 5; i++)
                    presenter.Refresh(window, CoverCatalog(), true, null, selected);
                Eq(1, parent.Children.OfType<Canvas>().Count());
                Eq(source, images[0].Source);
                Eq(binding, BindingOperations.GetBindingBase(images[0], Image.SourceProperty));
                Eq("original-cover-550.png", games[0].CoverImage);
                var overlay = parent.Children.OfType<Canvas>().Single();
                var badge = overlay.Children.OfType<CoverBadgeVisual>().Single();
                Eq(false, overlay.IsHitTestVisible);
                Eq(false, badge.IsHitTestVisible);
                Eq(false, badge.Focusable);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Eq(1, clicks);
                var hitPoint = images[0].TranslatePoint(new Point(75, 12), root);
                Eq(true, root.InputHitTest(hitPoint) is Image);
                Render(root, "cover-badges.png");
                selected.ControlPaused = true;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                var activeBadge = ((Grid)row.Children[1]).Children.OfType<Canvas>().Single().Children.OfType<CoverBadgeVisual>().Single();
                Eq("Paused", System.Windows.Automation.AutomationProperties.GetName(activeBadge));
                Render(root, "cover-badges-paused.png");
                selected.ControlPaused = false;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName(activeBadge));
                presenter.Refresh(window, CoverCatalog(), false);
                Eq(0, presenter.ActiveCount);
                Eq(0, parent.Children.OfType<Canvas>().Count());
                Eq(source, images[0].Source);
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(3, presenter.ActiveCount);
                presenter.Refresh(window, CoverCatalog("550:4:-1,551:4:-1,552:4:-1", "0", "0"), true);
                Eq(0, presenter.ActiveCount);
                Eq(0, parent.Children.OfType<Canvas>().Count());
                Eq(root, window.Content);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void CoverBadgeReuse()
    {
        Image image;
        var game = CoverGame(550);
        var tile = CoverTile(game, out image);
        image.Name = "CoverImg";
        Eq(true, CoverBadgePresenter.IsCover(image));
        var viewbox = new Viewbox
        {
            Width = 90,
            Height = 135,
            Child = tile,
            Stretch = System.Windows.Media.Stretch.Uniform
        };
        var root = new Grid();
        root.Children.Add(viewbox);
        var window = new TestWindow
        {
            Width = 280,
            Height = 250,
            Content = root,
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
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                var badge = tile.Children.OfType<Canvas>().Single().Children.OfType<CoverBadgeVisual>().Single();
                Eq(true, Math.Abs(tile.ActualWidth - badge.ActualWidth) < .01 && Math.Abs(tile.ActualHeight - badge.ActualHeight) < .01);
                tile.DataContext = new CoverContextFixture
                {
                    Game = CoverGame(551),
                    CoverImageObject = CoverArt(15, 85, 70)
                };
                Eq(Visibility.Collapsed, badge.Visibility);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                Eq("Updating", System.Windows.Automation.AutomationProperties.GetName(badge));
                tile.DataContext = new CoverContextFixture
                {
                    Game = new Game
                    {
                        Id = Guid.NewGuid()
                    },
                    CoverImageObject = image.Source
                };
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                Eq(0, presenter.ActiveCount);
                Eq(0, tile.Children.OfType<Canvas>().Count());
                tile.DataContext = new CoverContextFixture
                {
                    Game = game,
                    CoverImageObject = CoverArt(35, 55, 80)
                };
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                root.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, 20, 20));
                window.UpdateLayout();
                Eq(0, presenter.ActiveCount);
                root.Clip = null;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                window.Content = new Grid();
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                Eq(0, presenter.ActiveCount);
                Eq(0, tile.Children.OfType<Canvas>().Count());
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void CoverBadgeCroppedPortrait()
    {
        var root = new Grid();
        var tile = new Grid
        {
            Width = 200,
            Height = 200,
            DataContext = new CoverContextFixture
            {
                Game = CoverGame(550)
            }
        };
        root.Children.Add(tile);
        var image = new Image
        {
            Name = "PART_ImageCover",
            Source = CoverArt(35, 55, 80),
            Stretch = System.Windows.Media.Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        tile.Children.Add(image);
        var window = new TestWindow
        {
            Width = 320,
            Height = 320,
            Content = root,
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
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                var overlay = tile.Children.OfType<Canvas>().Single();
                var badge = overlay.Children.OfType<CoverBadgeVisual>().Single();
                var bounds = badge.TransformToVisual(tile).TransformBounds(new Rect(badge.RenderSize));
                Eq(true, bounds.Top >= 0 && bounds.Bottom <= tile.ActualHeight && bounds.Left >= 0 && bounds.Right <= tile.ActualWidth);
                Render(root, "cover-badge-cropped-portrait.png");
                // Explicit ancestor clipping also moves the label into the visible part.
                tile.Clip = new System.Windows.Media.RectangleGeometry(new Rect(60, 80, 100, 100));
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                bounds = badge.TransformToVisual(tile).TransformBounds(new Rect(badge.RenderSize));
                Eq(true, bounds.Top >= 80 && bounds.Bottom <= 180 && bounds.Left >= 60 && bounds.Right <= 160);
                tile.Clip = null;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                foreach (var artSize in new[]
                {
                    new Size(600, 800),
                    new Size(1024, 1024),
                    new Size(1920, 1080)
                }

                )
                {
                    image.Source = new System.Windows.Media.DrawingImage(new System.Windows.Media.GeometryDrawing(System.Windows.Media.Brushes.DarkSlateBlue, null, new System.Windows.Media.RectangleGeometry(new Rect(artSize))));
                    foreach (var stretch in new[]
                    {
                        System.Windows.Media.Stretch.UniformToFill,
                        System.Windows.Media.Stretch.Uniform,
                        System.Windows.Media.Stretch.Fill
                    }

                    )
                    {
                        image.Stretch = stretch;
                        window.UpdateLayout();
                        presenter.Refresh(window, CoverCatalog(), true);
                        window.UpdateLayout();
                        Eq(1, presenter.ActiveCount);
                        bounds = badge.TransformToVisual(tile).TransformBounds(new Rect(badge.RenderSize));
                        Eq(true, bounds.Top >= 0 && bounds.Bottom <= tile.ActualHeight && bounds.Left >= 0 && bounds.Right <= tile.ActualWidth);
                    }
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void CoverBadgeSdk()
    {
        var catalog = CoverCatalog();
        bool enabled = true;
        var game = CoverGame(550);
        Image image;
        var tile = CoverTile(game, out image);
        UpdateState selected = null;
        var sdk = new CoverBadgeControl(() => catalog, () => enabled, null, () => selected)
        {
            GameContext = game,
            VerticalAlignment = VerticalAlignment.Top
        };
        tile.Children.Add(sdk);
        var window = new TestWindow
        {
            Width = 260,
            Height = 320,
            Content = tile,
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
                sdk.Refresh();
                presenter.Refresh(window, catalog, true);
                Eq(Visibility.Visible, sdk.Visibility);
                Eq(0, presenter.ActiveCount);
                Eq(false, sdk.IsHitTestVisible);
                Eq(false, sdk.Focusable);
                Eq(true, sdk.ActualWidth > 100 && sdk.ActualHeight > 200);
                selected = new UpdateState
                {
                    GameId = game.Id,
                    Phase = UpdatePhase.Updating,
                    ControlPaused = false,
                    Display = new UpdateDisplay
                    {
                        WorkPercent = 93,
                        Downloaded = 62,
                        DownloadTotal = 100
                    }
                };
                sdk.Refresh();
                Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                selected.ControlPaused = true;
                sdk.Refresh();
                Eq("Paused", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                selected.ControlPaused = false;
                sdk.Refresh();
                Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                sdk.GameContext = CoverGame(551);
                Eq("Updating", System.Windows.Automation.AutomationProperties.GetName((CoverBadgeVisual)sdk.Content));
                enabled = false;
                sdk.Refresh();
                Eq(Visibility.Collapsed, sdk.Visibility);
                enabled = true;
                sdk.Refresh();
                Eq(Visibility.Visible, sdk.Visibility);
                catalog = CoverCatalog("550:4:-1,551:4:-1,552:4:-1", "0", "0");
                sdk.Refresh();
                Eq(Visibility.Collapsed, sdk.Visibility);
                catalog = CoverCatalog();
                sdk.Shutdown();
                sdk.Refresh();
                Eq(Visibility.Collapsed, sdk.Visibility);
            }
            finally
            {
                sdk.Shutdown();
                window.Close();
            }
        }
    }
}
