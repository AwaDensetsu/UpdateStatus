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
    private static ListBoxItem LibraryRow(Game game, out TextBlock title, bool thumbnail)
    {
        game.Name = "A long game name which must leave space for its update status";
        var grid = new Grid
        {
            Height = 50,
            Background = System.Windows.Media.Brushes.Transparent
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        if (thumbnail)
            grid.Children.Add(new Image { Name = "PART_ImageCover", Width = 32, Height = 40, Source = CoverArt(40, 60, 90) });
        else
            grid.Children.Add(new TextBlock { Text = "🎮", FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
        title = new TextBlock
        {
            FontSize = 17,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 5, 6, 5)
        };
        title.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        return new ListBoxItem
        {
            Content = grid,
            DataContext = game,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private static void LibraryRowBadges()
    {
        var root = new Grid
        {
            Background = System.Windows.Media.Brushes.Black
        };
        var list = new ListBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0)
        };
        root.Children.Add(list);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        TextBlock title, otherTitle;
        var game = CoverGame(550);
        var row = LibraryRow(game, out title, true);
        list.Items.Add(row);
        list.Items.Add(LibraryRow(CoverGame(551), out otherTitle, false));
        TextBlock foreignTitle;
        var foreign = CoverGame(552);
        foreign.PluginId = Guid.NewGuid();
        list.Items.Add(LibraryRow(foreign, out foreignTitle, false));
        var originalPadding = title.Padding;
        title.SetBinding(TextBlock.PaddingProperty, new Binding("Tag") { Source = root });
        root.Tag = originalPadding;
        var paddingBinding = BindingOperations.GetBindingBase(title, TextBlock.PaddingProperty);
        var textBinding = BindingOperations.GetBindingBase(title, TextBlock.TextProperty);
        var window = new TestWindow
        {
            Width = 660,
            Height = 290,
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
                Eq(0, presenter.ActiveCount);
                Eq(2, presenter.ActiveRowCount);
                Eq(2, Visuals<LibraryRowBadgeVisual>(root).Count());
                var badge = Visuals<LibraryRowBadgeVisual>((Grid)row.Content).Single();
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(badge));
                Eq(true, title.Padding.Right > originalPadding.Right);
                Eq(paddingBinding, BindingOperations.GetBindingBase(title, TextBlock.PaddingProperty));
                Eq(textBinding, BindingOperations.GetBindingBase(title, TextBlock.TextProperty));
                Eq(false, badge.IsHitTestVisible);
                Eq(false, badge.Focusable);
                var bounds = badge.TransformToVisual(title).TransformBounds(new Rect(badge.RenderSize));
                Eq(true, bounds.Left >= title.ActualWidth - title.Padding.Right && bounds.Right <= title.ActualWidth);
                Eq(true, root.InputHitTest(badge.TranslatePoint(new Point(5, 5), root)) != badge);
                var selected = new UpdateState
                {
                    GameId = game.Id,
                    Phase = UpdatePhase.Updating,
                    ControlPaused = false,
                    Display = new UpdateDisplay
                    {
                        WorkPercent = 93,
                        Downloaded = 62,
                        DownloadTotal = 100,
                        Stage = "Verifying files"
                    }
                };
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Render(root, "library-list-downloading-1.19.9.png");
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(badge));
                Eq("Updating", System.Windows.Automation.AutomationProperties.GetName(Visuals<LibraryRowBadgeVisual>((Grid)((ListBoxItem)list.Items[1]).Content).Single()));
                selected.ControlPaused = true;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Paused", System.Windows.Automation.AutomationProperties.GetName(badge));
                Render(root, "library-list-paused-1.18.png");
                selected.ControlPaused = false;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(badge));
                selected.Display.Downloaded = 100;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(badge));
                Render(root, "library-list-verifying-1.19.9.png");
                selected.Display.Stage = "Finalizing";
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Finalizing", System.Windows.Automation.AutomationProperties.GetName(badge));
                selected.Display.Downloaded = 62;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                for (int i = 0; i < 5; i++)
                {
                    presenter.Refresh(window, CoverCatalog(), true, null, selected);
                    window.UpdateLayout();
                }

                Eq(2, Visuals<LibraryRowBadgeVisual>(root).Count());
                window.Width = 290;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq(true, badge.ActualWidth < 90);
                Eq(true, title.ActualWidth - title.Padding.Right >= title.ActualWidth * .45);
                Render(root, "library-list-narrow-1.18.png");
                row.DataContext = new Game
                {
                    Id = Guid.NewGuid(),
                    Name = "Different game"
                };
                Eq(Visibility.Collapsed, badge.Visibility);
                Eq(originalPadding, title.Padding);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq(1, presenter.ActiveRowCount);
                row.DataContext = game;
                window.Width = 660;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(2, presenter.ActiveRowCount);
                list.FlowDirection = FlowDirection.RightToLeft;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(true, title.Padding.Left > originalPadding.Left);
                Eq(originalPadding.Right, title.Padding.Right);
                Render(root, "library-list-rtl-1.18.png");
                list.FlowDirection = FlowDirection.LeftToRight;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                root.Tag = new Thickness(9, 5, 12, 5);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), false);
                Eq(new Thickness(9, 5, 12, 5), title.Padding);
                Eq(paddingBinding, BindingOperations.GetBindingBase(title, TextBlock.PaddingProperty));
                Eq(0, Visuals<LibraryRowBadgeVisual>(root).Count());
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                list.Items.Remove(row);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                Eq(new Thickness(9, 5, 12, 5), title.Padding);
                Eq(1, presenter.ActiveRowCount);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void LibraryMixedSurfaces()
    {
        var root = new Grid
        {
            Background = System.Windows.Media.Brushes.Black
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        var first = CoverGame(550);
        var second = CoverGame(551);
        var third = CoverGame(552);
        Image cover;
        var tile = CoverTile(first, out cover);
        root.Children.Add(tile);
        var list = new ListBox
        {
            Foreground = System.Windows.Media.Brushes.White,
            Background = System.Windows.Media.Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(list, 1);
        root.Children.Add(list);
        TextBlock thumbnailTitle;
        var thumbnail = LibraryRow(first, out thumbnailTitle, true);
        list.Items.Add(thumbnail);
        var originalPadding = thumbnailTitle.Padding;
        var titleBinding = BindingOperations.GetBindingBase(thumbnailTitle, TextBlock.TextProperty);
        var wide = new Grid
        {
            Height = 60
        };
        var banner = new Image
        {
            Name = "PART_ImageCover",
            Source = CoverArt(40, 60, 90),
            Stretch = System.Windows.Media.Stretch.Fill
        };
        var wideTitle = new TextBlock
        {
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        wideTitle.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        wide.Children.Add(banner);
        wide.Children.Add(wideTitle);
        list.Items.Add(new ListBoxItem { Content = wide, DataContext = second, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var outer = new Grid
        {
            Height = 100
        };
        outer.RowDefinitions.Add(new RowDefinition());
        outer.RowDefinitions.Add(new RowDefinition());
        var outerTitle = new TextBlock
        {
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        outerTitle.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        outer.Children.Add(outerTitle);
        TextBlock nestedTitle;
        var nested = LibraryRow(first, out nestedTitle, false);
        Grid.SetRow(nested, 1);
        outer.Children.Add(nested);
        list.Items.Add(new ListBoxItem { Content = outer, DataContext = third, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var window = new TestWindow
        {
            Content = root,
            Width = 850,
            Height = 410
        };
        var sdk = new CoverBadgeControl(() => CoverCatalog(), () => true)
        {
            GameContext = first
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
                Eq(2, presenter.ActiveCount);
                Eq(3, presenter.ActiveRowCount);
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(Visuals<LibraryRowBadgeVisual>((Grid)thumbnail.Content).Single()));
                var outerBadge = Visuals<LibraryRowBadgeVisual>(outer).Single(badge => badge.TransformToVisual(outerTitle).Transform(new Point()).Y < outerTitle.ActualHeight);
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(outerBadge));
                Eq(1, Visuals<LibraryRowBadgeVisual>((Grid)nested.Content).Count());
                Eq(0, Visuals<LibraryRowBadgeVisual>(wide).Count());
                Render(root, "library-mixed-discovery-1.19.4.png");
                var selected = new UpdateState
                {
                    GameId = first.Id,
                    Phase = UpdatePhase.Updating,
                    ControlPaused = false,
                    Display = new UpdateDisplay
                    {
                        WorkPercent = 93,
                        Downloaded = 62,
                        DownloadTotal = 100,
                        Stage = "Installing files"
                    }
                };
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(Visuals<LibraryRowBadgeVisual>((Grid)thumbnail.Content).Single()));
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(Visuals<LibraryRowBadgeVisual>((Grid)nested.Content).Single()));
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(outerBadge));
                selected.ControlPaused = true;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Paused", System.Windows.Automation.AutomationProperties.GetName(Visuals<LibraryRowBadgeVisual>((Grid)nested.Content).Single()));
                Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(outerBadge));
                selected.ControlPaused = false;
                selected.Display.Downloaded = 100;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Installing files", System.Windows.Automation.AutomationProperties.GetName(Visuals<LibraryRowBadgeVisual>((Grid)nested.Content).Single()));
                Eq("Installing files", System.Windows.Automation.AutomationProperties.GetName(Visuals<CoverBadgeVisual>(tile).Single()));
                banner.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                Eq(4, presenter.ActiveRowCount);
                banner.Visibility = Visibility.Visible;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(2, presenter.ActiveCount);
                Eq(3, presenter.ActiveRowCount);
                tile.Children.Add(sdk);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(Visibility.Visible, sdk.Visibility);
                Eq(1, presenter.ActiveCount);
                Eq(3, presenter.ActiveRowCount);
                sdk.Shutdown();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(2, presenter.ActiveCount);
                list.Visibility = Visibility.Collapsed;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveCount);
                Eq(0, presenter.ActiveRowCount);
                Eq(originalPadding, thumbnailTitle.Padding);
                list.Visibility = Visibility.Visible;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(2, presenter.ActiveCount);
                Eq(3, presenter.ActiveRowCount);
                Eq(titleBinding, BindingOperations.GetBindingBase(thumbnailTitle, TextBlock.TextProperty));
                presenter.Refresh(window, CoverCatalog(), false);
                Eq(0, presenter.ActiveCount);
                Eq(0, presenter.ActiveRowCount);
                Eq(originalPadding, thumbnailTitle.Padding);
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(2, presenter.ActiveCount);
                Eq(3, presenter.ActiveRowCount);
                presenter.Refresh(window, CoverCatalog("550:4:-1,551:4:-1,552:4:-1", "0", "0"), true);
                window.UpdateLayout();
                Eq(0, presenter.ActiveCount);
                Eq(0, presenter.ActiveRowCount);
                Eq(originalPadding, thumbnailTitle.Padding);
            }
            finally
            {
                sdk.Shutdown();
                window.Close();
            }
        }
    }

    private static void LibraryAndCardProgress()
    {
        var game = CoverGame(550);
        var display = new UpdateDisplay
        {
            Downloaded = 62,
            DownloadTotal = 100,
            DownloadPercent = 62,
            WorkPercent = 93,
            Stage = "Verifying files"
        };
        var state = new UpdateState
        {
            GameId = game.Id,
            Phase = UpdatePhase.Updating,
            ControlPaused = false,
            Display = display,
            Percent = 93
        };
        using (var monitor = new UpdateMonitor((s, d) =>
        {
        }))
        {
            var row = new LibraryRowBadgeVisual();
            var cover = new CoverBadgeVisual();
            var card = new StatusControl(monitor, true)
            {
                Width = 400
            };
            Action refresh = () =>
            {
                var info = new CoverBadgeSnapshot(CoverCatalog(), null, state).Find(game);
                row.Configure(info, 16, 250);
                cover.Render(info);
                card.Render(state);
            };
            refresh();
            var panel = (StackPanel)((Border)card.Content).Child;
            var meters = panel.Children.OfType<ProgressBar>().ToArray();
            Eq(2, meters.Length);
            Eq(Visibility.Visible, meters[0].Visibility);
            Eq(Visibility.Visible, meters[1].Visibility);
            Eq(62d, meters[0].Value);
            Eq(93d, meters[1].Value);
            Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(row));
            Eq("Downloading — 62%", System.Windows.Automation.AutomationProperties.GetName(cover));
            Eq(true, panel.Children.OfType<TextBlock>().Any(text => text.Text == "Verifying files — 93%"));
            display.Downloaded = 100;
            display.DownloadPercent = 100;
            refresh();
            Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(row));
            Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(cover));
            Eq(100d, meters[0].Value);
            Eq(93d, meters[1].Value);
            display.Stage = "Finalizing";
            refresh();
            Eq("Finalizing", System.Windows.Automation.AutomationProperties.GetName(row));
            state.ControlPaused = true;
            display.Paused = true;
            refresh();
            Eq("Paused", System.Windows.Automation.AutomationProperties.GetName(row));
            Eq(2, meters.Count(meter => meter.Visibility == Visibility.Visible));
            Eq(false, meters[0].IsIndeterminate);
            Eq(false, meters[1].IsIndeterminate);
            state.Phase = UpdatePhase.Ready;
            Eq((CoverBadgeInfo)null, new CoverBadgeSnapshot(CoverCatalog(), null, state).Find(game));
        }
    }

    private static void LibraryTableBadges()
    {
        var game = CoverGame(550);
        game.Name = "Chess Ultra";
        var other = CoverGame(552);
        other.Name = "Chivalry 2";
        var table = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            RowHeight = 38,
            EnableRowVirtualization = true,
            ItemsSource = new[]
            {
                game,
                other
            }
        };
        var name = new DataGridTextColumn
        {
            Header = "Name",
            Binding = new Binding("Name"),
            Width = 330
        };
        table.Columns.Add(name);
        table.Columns.Add(new DataGridTextColumn { Header = "Other column", Binding = new Binding("GameId"), Width = 130 });
        var window = new TestWindow
        {
            Width = 640,
            Height = 240,
            Content = table,
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
                Eq(2, presenter.ActiveRowCount);
                var row = (DataGridRow)table.ItemContainerGenerator.ContainerFromItem(game);
                var badge = Visuals<LibraryRowBadgeVisual>(row).Single();
                var cell = Visuals<DataGridCell>(row).Single(c => c.Column == name);
                var title = Visuals<TextBlock>(cell).Single();
                var padding = new Thickness(0);
                var bounds = badge.TransformToVisual(cell).TransformBounds(new Rect(badge.RenderSize));
                Eq(true, bounds.Right <= cell.ActualWidth);
                Render(table, "library-table-1.18.png");
                var selected = new UpdateState
                {
                    GameId = game.Id,
                    Phase = UpdatePhase.Updating,
                    ControlPaused = false,
                    Display = new UpdateDisplay
                    {
                        Downloaded = 62,
                        DownloadTotal = 100,
                        WorkPercent = 93,
                        Stage = "Verifying files"
                    }
                };
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(badge));
                Render(table, "library-table-downloading-1.19.9.png");
                selected.Display.Downloaded = 100;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Verifying files", System.Windows.Automation.AutomationProperties.GetName(badge));
                Render(table, "library-table-verifying-1.19.9.png");
                selected.ControlPaused = true;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Paused", System.Windows.Automation.AutomationProperties.GetName(badge));
                name.Width = 110;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(true, badge.ActualWidth < 50);
                table.ItemsSource = new[]
                {
                    new Game
                    {
                        Id = Guid.NewGuid(),
                        Name = "Up to date"
                    }
                };
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(0, presenter.ActiveRowCount);
                Eq(padding, title.Padding);
                table.ItemsSource = new[]
                {
                    game,
                    other
                };
                name.Width = 330;
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(2, presenter.ActiveRowCount);
                table.Columns.Remove(name);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                Eq(0, presenter.ActiveRowCount);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void LibraryRowAdorner()
    {
        var game = CoverGame(550);
        game.Name = "Chess Ultra";
        var text = new TextBlock
        {
            FontSize = 16,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        var row = new ListBoxItem
        {
            Content = text,
            DataContext = game,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var list = new ListBox();
        list.Items.Add(row);
        // Force a Border/ContentPresenter template without any Grid.
        row.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListBoxItem'><Border><ContentPresenter HorizontalAlignment='Stretch'/></Border></ControlTemplate>");
        var decorator = new System.Windows.Documents.AdornerDecorator
        {
            Child = list
        };
        var window = new TestWindow
        {
            Width = 450,
            Height = 180,
            Content = decorator,
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
                Eq(1, presenter.ActiveRowCount);
                var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(text);
                Eq(1, layer.GetAdorners(text).Length);
                Eq(true, text.Padding.Right > 0);
                for (int i = 0; i < 3; i++)
                {
                    presenter.Refresh(window, CoverCatalog(), true);
                    window.UpdateLayout();
                }

                Eq(1, layer.GetAdorners(text).Length);
                window.Content = new Grid();
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                Eq(0, presenter.ActiveRowCount);
                Eq(new Thickness(0), text.Padding);
                Eq(true, layer.GetAdorners(text) == null || layer.GetAdorners(text).Length == 0);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void LibraryRowVirtualization()
    {
        var first = CoverGame(550);
        var last = CoverGame(552);
        var games = new List<Game>
        {
            first
        };
        games.AddRange(Enumerable.Range(0, 150).Select(i => new Game { Id = Guid.NewGuid(), Name = "Up to date " + i }));
        games.Add(last);
        var list = new ListBox
        {
            ItemsSource = games,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Height='40'><TextBlock Text='{Binding Name}' FontSize='16' VerticalAlignment='Center' TextTrimming='CharacterEllipsis'/></Grid></DataTemplate>");
        var root = new Grid();
        root.Children.Add(list);
        var window = new TestWindow
        {
            Width = 480,
            Height = 180,
            Content = root
        };
        using (var presenter = new CoverBadgePresenter((s, d) =>
        {
        }))
        {
            try
            {
                window.Show();
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveRowCount);
                var oldRow = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first);
                var oldText = Visuals<TextBlock>(oldRow).Single();
                list.ScrollIntoView(last);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(1, presenter.ActiveRowCount);
                Eq(true, Visuals<LibraryRowBadgeVisual>(root).Count() < 5);
                foreach (var row in Visuals<ListBoxItem>(root))
                {
                    var game = CoverGameContext.Resolve(row.DataContext);
                    foreach (var badge in Visuals<LibraryRowBadgeVisual>(row).Where(b => b.Visibility == Visibility.Visible))
                    {
                        Eq(last.Id, game.Id);
                        Eq("Update Required", System.Windows.Automation.AutomationProperties.GetName(badge));
                    }
                }

                list.ScrollIntoView(games[75]);
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
                {
                }));
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(0, presenter.ActiveRowCount);
                var coverList = new ListBox();
                Image image;
                var tile = CoverTile(first, out image);
                var coverRow = new ListBoxItem
                {
                    Content = tile,
                    DataContext = first,
                    Width = 175,
                    Height = 250
                };
                coverList.Items.Add(coverRow);
                root.Children.Clear();
                root.Children.Add(coverList);
                window.Height = 350;
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                window.UpdateLayout();
                Eq(0, presenter.ActiveRowCount);
                Eq(1, presenter.ActiveCount);
                Eq(new Thickness(0), oldText.Padding);
                presenter.Refresh(window, CoverCatalog(), false);
                Eq(0, presenter.ActiveCount);
                Eq(0, presenter.ActiveRowCount);
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void LibraryGridViewBadges()
    {
        var game = CoverGame(550);
        game.Name = "Chess Ultra";
        var view = new GridView();
        var name = new GridViewColumn
        {
            Header = "Name",
            DisplayMemberBinding = new Binding("Name"),
            Width = 320
        };
        view.Columns.Add(name);
        view.Columns.Add(new GridViewColumn { Header = "Identifier", DisplayMemberBinding = new Binding("GameId"), Width = 90 });
        var list = new ListView
        {
            View = view,
            ItemsSource = new[]
            {
                game
            }
        };
        var window = new TestWindow
        {
            Width = 530,
            Height = 180,
            Content = list
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
                Eq(1, presenter.ActiveRowCount);
                var row = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(game);
                Eq(1, Visuals<LibraryRowBadgeVisual>(window).Count());
                var badge = Visuals<LibraryRowBadgeVisual>(row).Single();
                var selected = new UpdateState
                {
                    GameId = game.Id,
                    Phase = UpdatePhase.Updating,
                    ControlPaused = false,
                    Display = new UpdateDisplay
                    {
                        Downloaded = 62,
                        DownloadTotal = 100,
                        WorkPercent = 93,
                        Stage = "Installing files"
                    }
                };
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Downloading 62%", System.Windows.Automation.AutomationProperties.GetName(badge));
                Render(list, "library-gridview-downloading-1.19.9.png");
                selected.Display.Downloaded = 100;
                presenter.Refresh(window, CoverCatalog(), true, null, selected);
                window.UpdateLayout();
                Eq("Installing files", System.Windows.Automation.AutomationProperties.GetName(badge));
                Render(list, "library-gridview-installing-1.19.9.png");
                view.Columns.Remove(name);
                window.UpdateLayout();
                presenter.Refresh(window, CoverCatalog(), true);
                Eq(0, presenter.ActiveRowCount);
            }
            finally
            {
                window.Close();
            }
        }
    }
}
