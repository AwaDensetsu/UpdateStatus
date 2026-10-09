using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PlayniteUpdateStatus.Launchers.Steam;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    // Owned overlays inside the existing cover group follow its clipping, z-order,
    // transforms and opacity. Image sources, templates and input remain intact.
    public sealed class CoverBadgePresenter : IDisposable
    {
        private sealed class Tile
        {
            public FrameworkElement Cover;
            public Guid GameId;
            public Grid Parent;
            public Canvas Canvas;
            public CoverBadgeVisual Badge;
        }
        private readonly Dictionary<FrameworkElement,Tile> tiles = new Dictionary<FrameworkElement,Tile>();
        private readonly HashSet<DependencyObject> owned = new HashSet<DependencyObject>();
        private readonly LibraryRowBadgePresenter rows = new LibraryRowBadgePresenter();
        private readonly Action<string,bool> log;
        private Window host;
        private FrameworkElement root;
        private bool positioning;
        private bool warnedContainer;
        public int ActiveCount { get { return tiles.Values.Count(tile => tile.Badge.Visibility == Visibility.Visible); } }
        public int ActiveRowCount { get { return rows.ActiveCount; } }
        public CoverBadgePresenter(Action<string,bool> log) { this.log = log; }
        public void Refresh(Window window, SteamCatalogSnapshot catalog, bool enabled, SteamCoverHistory history = null)
        { Refresh(window,catalog,enabled,history,null); }
        public void Refresh(Window window, SteamCatalogSnapshot catalog, bool enabled, SteamCoverHistory history, UpdateState selected)
        {
            if (!enabled || window == null || !window.IsVisible) { Dispose(); return; }
            var nextRoot = window.Content as FrameworkElement;
            if (nextRoot == null) { Dispose(); return; }
            if (host != window || root != nextRoot)
            {
                Dispose(); host = window; root = nextRoot;
                root.LayoutUpdated += LayoutChanged; host.Closed += HostClosed;
            }
            var snapshot = new CoverBadgeSnapshot(catalog,history,selected);
            var found = new Dictionary<FrameworkElement,CoverBadgeInfo>();
            var rowCandidates = new List<FrameworkElement>();
            var stack = new Stack<DependencyObject>(); stack.Push(root);
            int budget = 16000;
            while (stack.Count > 0 && budget-- > 0)
            {
                var node = stack.Pop(); if (owned.Contains(node) || rows.Owns(node) || node is CoverBadgeControl) continue;
                var element = node as FrameworkElement;
                if (element != null && (!element.IsVisible || element.Opacity == 0)) continue;
                // Discover both surfaces in the same bounded root traversal.
                // Row titles still get their own bounded, context-aware check.
                if (element is ListBoxItem || element is DataGridRow) rowCandidates.Add(element);
                if (element != null && IsCover(element) && !LibraryRowBadgePresenter.IsRowThumbnail(element))
                {
                    var game = CoverGameContext.Resolve(element.DataContext);
                    var badge = snapshot.Find(game);
                    if (badge != null && !HasSdkBadge(element,game.Id) && !VisibleBounds(element,root).IsEmpty) found[element] = badge;
                }
                for (int i = VisualTreeHelper.GetChildrenCount(node)-1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(node,i));
            }
            foreach (var old in tiles.Keys.Where(cover => !found.ContainsKey(cover)).ToList()) Remove(old);
            foreach (var pair in found)
            {
                Tile tile;
                Grid parent = ParentGrid(pair.Key);
                if (tiles.TryGetValue(pair.Key,out tile) && (tile.Parent != parent || !tile.Parent.Children.Contains(tile.Canvas))) { Remove(pair.Key); tile = null; }
                if (tile == null)
                {
                    if (parent == null) {
                        if (!warnedContainer) { warnedContainer = true; log("Cover has no supported overlay container; SDK Badge control remains available",true); }
                        continue;
                    }
                    var overlay = new Canvas { IsHitTestVisible = false, Focusable = false, ClipToBounds = true };
                    Grid.SetRowSpan(overlay,Math.Max(1,parent.RowDefinitions.Count)); Grid.SetColumnSpan(overlay,Math.Max(1,parent.ColumnDefinitions.Count));
                    parent.Children.Add(overlay);
                    CoverBadgeLayering.Place(parent,pair.Key,overlay);
                    tile = new Tile { Cover = pair.Key, Parent = parent, Canvas = overlay, Badge = new CoverBadgeVisual() };
                    overlay.Children.Add(tile.Badge); tiles.Add(pair.Key,tile); owned.Add(overlay);
                    pair.Key.DataContextChanged += ContextChanged; pair.Key.Unloaded += CoverUnloaded;
                }
                tile.GameId = pair.Value.GameId; tile.Badge.Render(pair.Value);
            }
            Position();
            rows.Refresh(root,snapshot,rowCandidates);
        }
        private void Remove(FrameworkElement cover)
        { var tile = tiles[cover]; cover.DataContextChanged -= ContextChanged; cover.Unloaded -= CoverUnloaded; tile.Parent.Children.Remove(tile.Canvas); owned.Remove(tile.Canvas); tiles.Remove(cover); }
        private void ContextChanged(object sender, DependencyPropertyChangedEventArgs args) { HideCover(sender as FrameworkElement); }
        private void CoverUnloaded(object sender, RoutedEventArgs args) { HideCover(sender as FrameworkElement); }
        private void HideCover(FrameworkElement cover) { Tile tile; if (cover != null && tiles.TryGetValue(cover,out tile)) tile.Badge.Visibility = Visibility.Collapsed; }
        private static Grid ParentGrid(DependencyObject element)
        {
            for (int depth = 0; element != null && depth < 32; depth++)
            {
                element = VisualTreeHelper.GetParent(element);
                var grid = element as Grid; if (grid != null) return grid;
            }
            return null;
        }
        public static bool IsCover(FrameworkElement element)
        {
            if (element.Name == "PART_ImageCover") return true;
            var image = element as Image; if (image == null) return false;
            return CoverBinding(BindingOperations.GetBindingBase(image,Image.SourceProperty));
        }
        private static bool CoverBinding(BindingBase binding)
        {
            var single = binding as Binding;
            if (single != null)
            {
                string path = single.Path == null ? null : single.Path.Path.Split('.').Last();
                return path == "CoverImage" || path == "CoverImageObject" || path == "CoverImagePath";
            }
            var multi = binding as MultiBinding; return multi != null && multi.Bindings.Any(CoverBinding);
        }
        private static bool HasSdkBadge(FrameworkElement cover, Guid id)
        {
            var panel = VisualTreeHelper.GetParent(cover) as Panel; if (panel == null) return false;
            // Refresh may move a widget behind its theme actions.
            foreach (var widget in panel.Children.OfType<CoverBadgeControl>().ToArray())
            {
                widget.Refresh(); var game = widget.GameContext ?? CoverGameContext.Resolve(widget.DataContext);
                if (widget.IsVisible && game != null && game.Id == id) return true;
            }
            return false;
        }
        public static Rect VisibleBounds(FrameworkElement element, Visual relativeTo)
        { return VisibleBounds(element,relativeTo,24); }
        public static Rect VisibleBounds(FrameworkElement element, Visual relativeTo, double minimumHeight)
        {
            try
            {
                if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return Rect.Empty;
                var rect = element.TransformToVisual(relativeTo).TransformBounds(new Rect(element.RenderSize));
                DependencyObject parent = element;
                while (parent != null)
                {
                    var ui = parent as UIElement;
                    if (ui != null)
                    {
                        if (!ui.IsVisible || ui.Opacity == 0) return Rect.Empty;
                        if (ui.ClipToBounds || ui is ScrollContentPresenter || parent == relativeTo)
                            rect.Intersect(ui.TransformToVisual(relativeTo).TransformBounds(new Rect(ui.RenderSize)));
                        // WPF adds a layout clip for UniformToFill and constrained
                        // images even when the public Clip property is null.
                        var clip = VisualTreeHelper.GetClip(ui);
                        if (clip != null) rect.Intersect(ui.TransformToVisual(relativeTo).TransformBounds(clip.Bounds));
                    }
                    if (parent == relativeTo) break;
                    parent = VisualTreeHelper.GetParent(parent);
                }
                if (rect.IsEmpty || rect.Width < 40 || rect.Height < minimumHeight || double.IsNaN(rect.X) || double.IsInfinity(rect.X)) return Rect.Empty;
                return rect;
            }
            catch (InvalidOperationException) { return Rect.Empty; } catch (ArgumentException) { return Rect.Empty; }
        }
        private void LayoutChanged(object sender, EventArgs e) { Position(); }
        private void HostClosed(object sender, EventArgs e) { Dispose(); }
        private void Position()
        {
            if (positioning || root == null) return;
            positioning = true;
            try
            {
                foreach (var tile in tiles.Values)
                {
                    var game = CoverGameContext.Resolve(tile.Cover.DataContext);
                    var visible = VisibleBounds(tile.Cover,root);
                    if (game == null || game.Id != tile.GameId || visible.IsEmpty)
                    { tile.Badge.Visibility = Visibility.Collapsed; continue; }
                    try
                    {
                        CoverBadgeLayering.Place(tile.Parent,tile.Cover,tile.Canvas);
                        // Anchor to the visible cover, not its full rendered bitmap.
                        // Portrait art in a square tile extends above/below its slot.
                        var bounds = root.TransformToVisual(tile.Canvas).TransformBounds(visible);
                        bounds.Intersect(new Rect(tile.Canvas.RenderSize));
                        if (bounds.IsEmpty) { tile.Badge.Visibility = Visibility.Collapsed; continue; }
                        var transform = tile.Parent.TransformToVisual(root);
                        var origin = transform.Transform(new Point()); var axis = transform.Transform(new Point(1,0));
                        double scale = (axis-origin).Length;
                        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale)) { tile.Badge.Visibility = Visibility.Collapsed; continue; }
                        scale = Math.Max(0.1,Math.Min(10,scale));
                        tile.Badge.SetScale(scale);
                        tile.Badge.Visibility = Visibility.Visible;
                        tile.Badge.Width = bounds.Width; tile.Badge.Height = bounds.Height;
                        tile.Badge.Measure(bounds.Size);
                        Canvas.SetLeft(tile.Badge,bounds.Left);
                        Canvas.SetTop(tile.Badge,bounds.Top);
                    }
                    catch (InvalidOperationException) { tile.Badge.Visibility = Visibility.Collapsed; }
                }
            }
            finally { positioning = false; }
        }
        public void Dispose()
        {
            rows.Dispose();
            if (root != null) root.LayoutUpdated -= LayoutChanged;
            if (host != null) host.Closed -= HostClosed;
            foreach (var tile in tiles.Values) { tile.Cover.DataContextChanged -= ContextChanged; tile.Cover.Unloaded -= CoverUnloaded; tile.Parent.Children.Remove(tile.Canvas); }
            tiles.Clear(); owned.Clear(); root = null; host = null; warnedContainer = false;
        }
    }
}
