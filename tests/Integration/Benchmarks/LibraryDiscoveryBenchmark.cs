using static UpdateStatus.Tests.Support.SteamSamples;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.UI;

// Native WPF views, synthetic state, no installed plugin or Steam calls.
internal static class LibraryDiscoveryBenchmark
{
    public static int Run()
    {
        new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
        AppDomain.MonitoringIsEnabled = true;
        Console.WriteLine("Library refresh benchmark; CLR " + Environment.Version + "; 64-bit=" + Environment.Is64BitProcess);
        Console.WriteLine("Five samples of 50 Refresh + UpdateLayout calls, after five warmups; synthetic data, off-screen WPF windows.");
        Measure("200 cover tiles", Covers(200), null);
        var list = List(10000, true);
        Measure("10,000-game virtualized list", list, null);
        Measure("10,000-game virtualized scrolling", list, pass => Find<ScrollViewer>(list).ScrollToVerticalOffset(pass % 2 == 0 ? 0 : 180));
        var mixed = new Grid();
        mixed.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        mixed.ColumnDefinitions.Add(new ColumnDefinition());
        mixed.Children.Add(Covers(80));
        var rows = List(300, false);
        Grid.SetColumn(rows, 1);
        mixed.Children.Add(rows);
        Measure("80 covers and 300 nonvirtualized rows", mixed, null);
        return 0;
    }

    private static void Measure(string name, FrameworkElement view, Action<int> change)
    {
        var ids = Enumerable.Range(1, 1024).Select(i => (uint)i).ToArray();
        var apps = ids.Select(i => new SteamCatalogApp(i, "Synthetic game")).ToArray();
        var sample = SteamQueueSample.Parse("Q3|1|0|0|1|" + string.Join(",", ids.Select(i => i + ":6:-1")) + "|-", ids);
        sample.ReceivedUtc = DateTime.UtcNow;
        var history = new SteamCoverHistory(null, (s, d) =>
        {
        });
        history.Observe(new SteamCatalogSnapshot(apps, sample, null));
        var offline = new SteamCatalogSnapshot(apps, null, "Synthetic offline history");
        var window = new Window
        {
            Content = view,
            Width = 820,
            Height = 620,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        var elapsed = new double[5];
        var allocated = new long[5];
        long checksum = 0;
        using (var presenter = new CoverBadgePresenter((s, d) =>
        {
        }))
        {
            try
            {
                window.Show();
                window.UpdateLayout();
                for (int pass = 0; pass < 5; pass++)
                {
                    presenter.Refresh(window, offline, true, history);
                    window.UpdateLayout();
                }

                int nodes = CountNodes(view), realized = CountNodes(view, node => node is ListBoxItem);
                int covers = presenter.ActiveCount, rows = presenter.ActiveRowCount;
                if (covers + rows == 0)
                    throw new Exception("Benchmark view did not realize any indicators.");
                for (int run = 0; run < elapsed.Length; run++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    long before = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
                    var watch = Stopwatch.StartNew();
                    for (int pass = 0; pass < 50; pass++)
                    {
                        if (change != null)
                        {
                            change(pass);
                            window.UpdateLayout();
                        }

                        presenter.Refresh(window, offline, true, history);
                        window.UpdateLayout();
                        checksum += presenter.ActiveCount + presenter.ActiveRowCount;
                    }

                    watch.Stop();
                    elapsed[run] = watch.Elapsed.TotalMilliseconds;
                    allocated[run] = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - before;
                }

                Array.Sort(elapsed);
                Array.Sort(allocated);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0}: {1:F2} ms/50 calls ({2:F3} ms/call); {3:F2} MiB allocated; nodes={4}; realizedRows={5}; badges={6}/{7}; checksum={8}", name, elapsed[2], elapsed[2] / 50, allocated[2] / 1048576.0, nodes, realized, covers, rows, checksum));
            }
            finally
            {
                presenter.Dispose();
                window.Content = null;
                window.Close();
            }
        }
    }

    private static Game Game(int index)
    {
        return new Game
        {
            Id = Guid.NewGuid(),
            GameId = (index + 1).ToString(CultureInfo.InvariantCulture),
            PluginId = index < 1024 ? SteamGameIdentity.LibraryId : Guid.Empty,
            Name = "Synthetic library game " + index,
            IsInstalled = true
        };
    }

    private static FrameworkElement Covers(int count)
    {
        var panel = new WrapPanel();
        var art = new DrawingImage(new GeometryDrawing(Brushes.SlateGray, null, new RectangleGeometry(new Rect(0, 0, 90, 135))));
        art.Freeze();
        for (int i = 0; i < count; i++)
        {
            var tile = new Grid
            {
                Width = 96,
                Height = 150,
                Margin = new Thickness(4),
                DataContext = Game(i)
            };
            tile.RowDefinitions.Add(new RowDefinition());
            tile.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            tile.Children.Add(new Image { Name = "PART_ImageCover", Source = art });
            var title = new TextBlock
            {
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            Grid.SetRow(title, 1);
            tile.Children.Add(title);
            panel.Children.Add(tile);
        }

        return new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
    }

    private static ListBox List(int count, bool virtualized)
    {
        var list = new ListBox
        {
            ItemsSource = Enumerable.Range(0, count).Select(Game).ToArray(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        VirtualizingPanel.SetIsVirtualizing(list, virtualized);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, virtualized);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var panel = new FrameworkElementFactory(typeof(Grid));
        panel.SetValue(FrameworkElement.HeightProperty, 32.0);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetValue(TextBlock.FontSizeProperty, 16.0);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        title.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        title.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        panel.AppendChild(title);
        list.ItemTemplate = new DataTemplate
        {
            VisualTree = panel
        };
        return list;
    }

    private static T Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            var result = node as T;
            if (result != null)
                return result;
            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(node, i));
        }

        throw new Exception("Missing " + typeof(T).Name);
    }

    private static int CountNodes(DependencyObject root, Func<DependencyObject, bool> filter = null)
    {
        int count = 0;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (filter == null || filter(node))
                count++;
            for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                pending.Push(VisualTreeHelper.GetChild(node, i));
        }

        return count;
    }
}
