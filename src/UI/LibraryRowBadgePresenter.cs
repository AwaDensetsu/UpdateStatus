using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace PlayniteUpdateStatus.UI
{
    // Recognizes standard WPF item containers and public Game title bindings.
    // No theme name, item title string or private Playnite type is required.
    internal sealed class LibraryRowBadgePresenter : IDisposable
    {
        private sealed class Entry
        {
            public FrameworkElement Row;
            public TextBlock Title;
            public Guid GameId;
            public Thickness OriginalPadding, AppliedPadding;
            public Grid Parent;
            public Canvas Canvas;
            public AdornerLayer Layer;
            public RowAdorner Adorner;
            public LibraryRowBadgeVisual Badge;
            public CoverBadgeInfo Info;
        }
        private sealed class RowAdorner : Adorner
        {
            private readonly LibraryRowBadgeVisual badge;
            public double BadgeWidth;
            public Thickness TitlePadding;
            public RowAdorner(TextBlock title, LibraryRowBadgeVisual badge) : base(title)
            { this.badge = badge; IsHitTestVisible = false; Focusable = false; ClipToBounds = true; AddVisualChild(badge); }
            protected override int VisualChildrenCount { get { return 1; } }
            protected override Visual GetVisualChild(int index) { if (index != 0) throw new ArgumentOutOfRangeException("index"); return badge; }
            protected override Size MeasureOverride(Size constraint) { badge.Measure(new Size(BadgeWidth,AdornedElement.RenderSize.Height)); return AdornedElement.RenderSize; }
            protected override Size ArrangeOverride(Size size)
            { double left = FlowDirection == FlowDirection.RightToLeft ? TitlePadding.Left : Math.Max(0,size.Width-BadgeWidth-TitlePadding.Right); badge.Arrange(new Rect(left,0,BadgeWidth,size.Height)); return size; }
        }
        private readonly Dictionary<TextBlock,Entry> entries = new Dictionary<TextBlock,Entry>();
        private readonly HashSet<DependencyObject> owned = new HashSet<DependencyObject>();
        private FrameworkElement root;
        private bool positioning;
        public int ActiveCount { get { return entries.Values.Count(e => e.Badge.Visibility == Visibility.Visible); } }
        public bool Owns(DependencyObject node) { return owned.Contains(node); }
        public static FrameworkElement ItemContainer(DependencyObject node)
        {
            for (int depth = 0; node != null && depth < 40; depth++,node = VisualTreeHelper.GetParent(node))
                if (node is ListBoxItem || node is DataGridRow) return (FrameworkElement)node;
            return null;
        }
        public static bool IsCompactRow(FrameworkElement row)
        { return row != null && (row is DataGridRow || (row.ActualHeight >= 14 && row.ActualWidth >= row.ActualHeight * 2.5)); }
        public static bool IsRowThumbnail(FrameworkElement cover)
        {
            var row = ItemContainer(cover);
            return IsCompactRow(row) && (cover.ActualWidth < row.ActualWidth*.35 || cover.ActualHeight < row.ActualHeight*.8);
        }
        private static bool TitleBinding(TextBlock text)
        {
            var binding = BindingOperations.GetBinding(text,TextBlock.TextProperty);
            if (IsTitleBinding(binding)) return true;
            // GridView DisplayMemberBinding places the game field on its cell's
            // ContentPresenter, then supplies a plain string to a TextBlock.
            for (var node = VisualTreeHelper.GetParent(text); node != null && !(node is ListBoxItem) && !(node is DataGridRow); node = VisualTreeHelper.GetParent(node))
            {
                var cell = node as ContentPresenter;
                if (cell != null && IsTitleBinding(BindingOperations.GetBinding(cell,ContentPresenter.ContentProperty))) return true;
            }
            return false;
        }
        private static bool IsTitleBinding(Binding binding)
        {
            if (binding == null || binding.Path == null || binding.Source != null || !string.IsNullOrEmpty(binding.ElementName) || binding.RelativeSource != null) return false;
            var path = binding.Path.Path.Split('.');
            return (path.Last() == "Name" || path.Last() == "DisplayName") && path.Take(path.Length-1).All(p => p == "Game");
        }
        private static Playnite.SDK.Models.Game TitleGame(TextBlock text)
        { var row = ItemContainer(text); return CoverGameContext.Resolve(text.DataContext) ?? (row == null ? null : CoverGameContext.Resolve(row.DataContext)); }
        public void Refresh(FrameworkElement nextRoot, CoverBadgeSnapshot snapshot, IEnumerable<FrameworkElement> candidates)
        {
            if (root != nextRoot) { Dispose(); root = nextRoot; root.LayoutUpdated += LayoutChanged; }
            var found = new Dictionary<TextBlock,Entry>();
            foreach (var row in candidates)
            {
                if (IsCompactRow(row))
                {
                    var game = CoverGameContext.Resolve(row.DataContext); var info = snapshot.Find(game);
                    if (info != null)
                    {
                        var title = FindTitle(row,game.Id);
                        if (title != null && !CoverBadgePresenter.VisibleBounds(title,root,14).IsEmpty)
                            found[title] = new Entry { Row = row, Title = title, GameId = game.Id, Info = info };
                    }
                }
            }
            foreach (var old in entries.Keys.Where(t => !found.ContainsKey(t)).ToList()) Remove(old);
            foreach (var pair in found)
            {
                Entry entry;
                if (entries.TryGetValue(pair.Key,out entry) && (entry.Row != pair.Value.Row || entry.Parent != ParentGrid(pair.Key,pair.Value.Row) || (entry.Canvas != null && !entry.Parent.Children.Contains(entry.Canvas))))
                { Remove(pair.Key); entry = null; }
                if (entry == null)
                {
                    entry = pair.Value; entry.OriginalPadding = entry.AppliedPadding = entry.Title.Padding;
                    entry.Badge = new LibraryRowBadgeVisual(); entry.Parent = ParentGrid(entry.Title,entry.Row);
                    if (entry.Parent != null)
                    {
                        entry.Canvas = new Canvas { IsHitTestVisible = false, Focusable = false, ClipToBounds = true };
                        Grid.SetRowSpan(entry.Canvas,Math.Max(1,entry.Parent.RowDefinitions.Count)); Grid.SetColumnSpan(entry.Canvas,Math.Max(1,entry.Parent.ColumnDefinitions.Count));
                        Panel.SetZIndex(entry.Canvas,int.MaxValue-1); entry.Parent.Children.Add(entry.Canvas); entry.Canvas.Children.Add(entry.Badge); owned.Add(entry.Canvas);
                    }
                    else
                    {
                        entry.Layer = AdornerLayer.GetAdornerLayer(entry.Title); if (entry.Layer == null) continue;
                        entry.Adorner = new RowAdorner(entry.Title,entry.Badge); entry.Layer.Add(entry.Adorner); owned.Add(entry.Adorner);
                    }
                    entries.Add(entry.Title,entry); entry.Row.DataContextChanged += ContextChanged; entry.Title.DataContextChanged += ContextChanged; entry.Row.Unloaded += RowUnloaded;
                }
                entry.GameId = pair.Value.GameId; entry.Info = pair.Value.Info;
            }
            Position();
        }
        private TextBlock FindTitle(FrameworkElement row, Guid id)
        {
            TextBlock title = null; var stack = new Stack<DependencyObject>(); stack.Push(row); int budget = 1000;
            while (stack.Count > 0 && budget-- > 0)
            {
                var node = stack.Pop(); if (owned.Contains(node) || node is CoverBadgeControl || (node != row && (node is ListBoxItem || node is DataGridRow))) continue;
                var element = node as FrameworkElement; if (element != null && (!element.IsVisible || element.Opacity == 0)) continue;
                if (element != null && CoverBadgePresenter.IsCover(element) && !IsRowThumbnail(element) && !CoverBadgePresenter.VisibleBounds(element,root).IsEmpty) return null;
                var text = node as TextBlock;
                if (text != null && TitleBinding(text))
                { var game = TitleGame(text); if (game != null && game.Id == id && (title == null || text.ActualWidth > title.ActualWidth)) title = text; }
                for (int i = VisualTreeHelper.GetChildrenCount(node)-1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node,i));
            }
            return title;
        }
        private static Grid ParentGrid(DependencyObject title, FrameworkElement row)
        {
            for (var node = VisualTreeHelper.GetParent(title); node != null && node != row; node = VisualTreeHelper.GetParent(node))
            { var grid = node as Grid; if (grid != null) return grid; }
            return null;
        }
        private void ContextChanged(object sender, DependencyPropertyChangedEventArgs args)
        { foreach (var entry in entries.Values.Where(e => e.Row == sender || e.Title == sender).ToList()) { entry.Badge.Visibility = Visibility.Collapsed; RestorePadding(entry); } }
        private void RowUnloaded(object sender, RoutedEventArgs args)
        { foreach (var entry in entries.Values.Where(e => e.Row == sender).ToList()) { entry.Badge.Visibility = Visibility.Collapsed; RestorePadding(entry); } }
        private static void RestorePadding(Entry entry)
        {
            if (entry.Title.Padding == entry.AppliedPadding) entry.Title.SetCurrentValue(TextBlock.PaddingProperty,entry.OriginalPadding);
            entry.OriginalPadding = entry.AppliedPadding = entry.Title.Padding;
        }
        private void LayoutChanged(object sender, EventArgs args) { Position(); }
        private void Position()
        {
            if (positioning || root == null) return; positioning = true;
            try
            {
                foreach (var entry in entries.Values)
                {
                    var game = CoverGameContext.Resolve(entry.Row.DataContext); var titleGame = TitleGame(entry.Title);
                    var visible = CoverBadgePresenter.VisibleBounds(entry.Title,root,14);
                    if (!entry.Row.IsLoaded || !entry.Title.IsLoaded || game == null || titleGame == null || game.Id != entry.GameId || titleGame.Id != entry.GameId || !IsCompactRow(entry.Row) || visible.IsEmpty)
                    { entry.Badge.Visibility = Visibility.Collapsed; RestorePadding(entry); continue; }
                    try
                    {
                        if (entry.Title.Padding != entry.AppliedPadding) entry.OriginalPadding = entry.Title.Padding;
                        double maxWidth = Math.Max(0,entry.Title.ActualWidth * .45 - entry.OriginalPadding.Left - entry.OriginalPadding.Right);
                        double width = entry.Badge.Configure(entry.Info,entry.Title.FontSize,maxWidth,entry.Title.Foreground);
                        if (width < 12) { entry.Badge.Visibility = Visibility.Collapsed; RestorePadding(entry); continue; }
                        bool rtl = entry.Title.FlowDirection == FlowDirection.RightToLeft;
                        entry.AppliedPadding = new Thickness(entry.OriginalPadding.Left+(rtl ? width+10 : 0),entry.OriginalPadding.Top,entry.OriginalPadding.Right+(rtl ? 0 : width+10),entry.OriginalPadding.Bottom);
                        if (entry.Title.Padding != entry.AppliedPadding) entry.Title.SetCurrentValue(TextBlock.PaddingProperty,entry.AppliedPadding);
                        entry.Badge.Visibility = Visibility.Visible;
                        if (entry.Canvas != null)
                        {
                            var bounds = entry.Title.TransformToVisual(entry.Canvas).TransformBounds(new Rect(entry.Title.RenderSize));
                            var clipped = root.TransformToVisual(entry.Canvas).TransformBounds(visible); clipped.Intersect(new Rect(entry.Canvas.RenderSize));
                            entry.Badge.Width = width; entry.Badge.Height = bounds.Height;
                            double left = rtl ? bounds.Left+entry.OriginalPadding.Left : bounds.Right-entry.OriginalPadding.Right-width;
                            Canvas.SetLeft(entry.Badge,left); Canvas.SetTop(entry.Badge,bounds.Top);
                            var badgeBounds = new Rect(left,bounds.Top,width,bounds.Height);
                            badgeBounds.Intersect(clipped);
                            if (badgeBounds.IsEmpty) entry.Badge.Visibility = Visibility.Collapsed;
                            var clip = badgeBounds.IsEmpty ? new Rect(0,0,0,0) : new Rect(badgeBounds.Left-left,badgeBounds.Top-bounds.Top,badgeBounds.Width,badgeBounds.Height);
                            var oldClip = entry.Badge.Clip as RectangleGeometry;
                            if (oldClip == null || oldClip.Rect != clip) entry.Badge.Clip = new RectangleGeometry(clip);
                        }
                        else
                        {
                            var clip = root.TransformToVisual(entry.Title).TransformBounds(visible); clip.Intersect(new Rect(entry.Title.RenderSize));
                            var oldClip = entry.Adorner.Clip as RectangleGeometry;
                            if (oldClip == null || oldClip.Rect != clip) entry.Adorner.Clip = new RectangleGeometry(clip);
                            entry.Adorner.FlowDirection = entry.Title.FlowDirection;
                            if (entry.Adorner.TitlePadding != entry.OriginalPadding) { entry.Adorner.TitlePadding = entry.OriginalPadding; entry.Adorner.InvalidateArrange(); }
                            if (entry.Adorner.BadgeWidth != width) { entry.Adorner.BadgeWidth = width; entry.Adorner.InvalidateMeasure(); }
                        }
                    }
                    catch (InvalidOperationException) { entry.Badge.Visibility = Visibility.Collapsed; RestorePadding(entry); }
                }
            }
            finally { positioning = false; }
        }
        private void Remove(TextBlock title)
        {
            var entry = entries[title]; RestorePadding(entry); entry.Row.DataContextChanged -= ContextChanged; entry.Title.DataContextChanged -= ContextChanged; entry.Row.Unloaded -= RowUnloaded;
            if (entry.Canvas != null) { entry.Parent.Children.Remove(entry.Canvas); owned.Remove(entry.Canvas); }
            else { entry.Layer.Remove(entry.Adorner); owned.Remove(entry.Adorner); }
            entries.Remove(title);
        }
        public void Dispose()
        { if (root != null) root.LayoutUpdated -= LayoutChanged; foreach (var title in entries.Keys.ToList()) Remove(title); root = null; owned.Clear(); }
    }
}
