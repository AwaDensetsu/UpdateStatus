using System;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    // Uses the host's WPF surface, independent of theme names and named controls.
    // No popup window, content replacement, theme-file edit or input interception.
    public sealed class UniversalStatusPresenter : IDisposable
    {
        private readonly UpdateMonitor monitor;
        private readonly Action<string, bool> log;
        private Window host;
        private UIElement root;
        private AdornerLayer layer;
        private StatusAdorner adorner;
        private Grid fallback;
        private Canvas overlay;
        private StatusControl control;
        private bool warned;
        public UniversalStatusPresenter(UpdateMonitor monitor, Action<string, bool> log) { this.monitor = monitor; this.log = log; }
        public bool Refresh(Window window, UpdateState state, bool fullscreen)
        {
            if (window == null || !state.Visible || state.Dismissed || !window.IsVisible) { Dispose(); return false; }
            window.ApplyTemplate();
            UIElement nextRoot = window.Content as UIElement;
            var decorator = nextRoot as AdornerDecorator;
            if (decorator != null) nextRoot = decorator.Child;
            if (nextRoot == null) nextRoot = VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window,0) as UIElement : null;
            if (host != window || root != nextRoot || control == null || layer != null && !IsAttached())
            {
                Dispose(); host = window; root = nextRoot;
                if (root == null) return Unavailable();
                layer = AdornerLayer.GetAdornerLayer(root);
                control = new StatusControl(monitor,true,true,true);
                if (layer != null)
                {
                    adorner = new StatusAdorner(root,control); layer.Add(adorner);
                }
                else
                {
                    fallback = FindRootGrid(root);
                    if (fallback == null) { control = null; return Unavailable(); }
                    overlay = new Canvas { IsHitTestVisible = false, Focusable = false, ClipToBounds = true };
                    overlay.Children.Add(control); Grid.SetRow(overlay,0); Grid.SetColumn(overlay,0);
                    Grid.SetRowSpan(overlay,Math.Max(1,fallback.RowDefinitions.Count)); Grid.SetColumnSpan(overlay,Math.Max(1,fallback.ColumnDefinitions.Count));
                    Panel.SetZIndex(overlay,int.MaxValue); fallback.Children.Add(overlay);
                    overlay.SizeChanged += OverlaySizeChanged;
                }
                host.Closed += HostClosed;
                log("Attached theme-independent status surface (" + (layer != null ? "adorner" : "root grid") + ")",false);
            }
            control.Render(state);
            if (adorner != null) adorner.IsHitTestVisible = control.HasInteractiveControls;
            if (overlay != null) overlay.IsHitTestVisible = control.HasInteractiveControls;
            if (adorner != null) { adorner.Fullscreen = fullscreen; adorner.InvalidateMeasure(); }
            else { overlay.Tag = fullscreen; PositionOverlay(); }
            return true;
        }
        private bool IsAttached()
        {
            var current = layer.GetAdorners(root);
            if (current == null) return false;
            foreach (var item in current) if (item == adorner) return true;
            return false;
        }
        private bool Unavailable()
        { if (!warned) { warned = true; log("No usable WPF status surface; SDK theme control remains available.",false); } return false; }
        private static Grid FindRootGrid(DependencyObject node)
        {
            var grid = node as Grid; if (grid != null) return grid;
            for (int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++) { var found = FindRootGrid(VisualTreeHelper.GetChild(node,i)); if (found != null) return found; }
            return null;
        }
        private void OverlaySizeChanged(object sender, SizeChangedEventArgs e) { PositionOverlay(); }
        private void PositionOverlay()
        {
            if (overlay == null || control == null) return;
            bool fullscreen = overlay.Tag is bool && (bool)overlay.Tag;
            double width = Math.Max(0,Math.Min(fullscreen ? 560 : 480,overlay.ActualWidth-40));
            control.Width = width; control.Measure(new Size(width,double.PositiveInfinity));
            Canvas.SetLeft(control,fullscreen ? Math.Max(20,(overlay.ActualWidth-width)/2) : Math.Max(20,overlay.ActualWidth-width-20));
            Canvas.SetTop(control,Math.Max(20,overlay.ActualHeight-control.DesiredSize.Height-(fullscreen ? 90 : 24)));
        }
        private void HostClosed(object sender, EventArgs e) { Dispose(); }
        public void Dispose()
        {
            if (host != null) host.Closed -= HostClosed;
            if (layer != null && adorner != null) layer.Remove(adorner);
            if (fallback != null && overlay != null) { overlay.SizeChanged -= OverlaySizeChanged; fallback.Children.Remove(overlay); }
            host = null; root = null; layer = null; adorner = null; fallback = null; overlay = null; control = null;
        }
        private sealed class StatusAdorner : Adorner
        {
            private readonly StatusControl card;
            public bool Fullscreen;
            public StatusAdorner(UIElement element, StatusControl card) : base(element)
            { this.card = card; IsHitTestVisible = false; Focusable = false; AddVisualChild(card); AddLogicalChild(card); }
            protected override int VisualChildrenCount { get { return 1; } }
            protected override Visual GetVisualChild(int index) { if (index != 0) throw new ArgumentOutOfRangeException("index"); return card; }
            protected override IEnumerator LogicalChildren { get { return new[] { card }.GetEnumerator(); } }
            // Hit-test children only: the otherwise transparent full-window adorner
            // must never block the theme's buttons outside the card.
            protected override HitTestResult HitTestCore(PointHitTestParameters parameters) { return null; }
            protected override Size MeasureOverride(Size constraint)
            {
                double width = Math.Max(0,Math.Min(Fullscreen ? 560 : 480,AdornedElement.RenderSize.Width-40));
                card.Width = width; card.Measure(new Size(width,double.PositiveInfinity)); return AdornedElement.RenderSize;
            }
            protected override Size ArrangeOverride(Size finalSize)
            {
                double width = card.Width, height = card.DesiredSize.Height;
                double x = Fullscreen ? Math.Max(20,(finalSize.Width-width)/2) : Math.Max(20,finalSize.Width-width-20);
                double y = Math.Max(20,finalSize.Height-height-(Fullscreen ? 90 : 24));
                card.Arrange(new Rect(x,y,width,height)); return finalSize;
            }
        }
    }
}
