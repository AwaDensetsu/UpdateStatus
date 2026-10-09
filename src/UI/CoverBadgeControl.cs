using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteUpdateStatus.Core;
using Playnite.SDK.Controls;
using PlayniteUpdateStatus.Launchers.Steam;

namespace PlayniteUpdateStatus.UI
{
    // Optional SDK integration for layouts that do not expose a recognizable cover.
    public sealed class CoverBadgeControl : PluginUserControl
    {
        private readonly Func<SteamCatalogSnapshot> catalog;
        private readonly Func<bool> enabled;
        private readonly Func<SteamCoverHistory> history;
        private readonly Func<UpdateState> selected;
        private readonly CoverBadgeVisual visual = new CoverBadgeVisual();
        private DispatcherTimer timer;
        private bool stopped;
        public CoverBadgeControl(Func<SteamCatalogSnapshot> catalog, Func<bool> enabled, Func<SteamCoverHistory> history = null)
            : this(catalog,enabled,history,null) { }
        public CoverBadgeControl(Func<SteamCatalogSnapshot> catalog, Func<bool> enabled, Func<SteamCoverHistory> history, Func<UpdateState> selected)
        {
            this.catalog = catalog; this.enabled = enabled; this.history = history; this.selected = selected;
            Content = visual; IsHitTestVisible = false; Focusable = false; IsTabStop = false;
            HorizontalAlignment = HorizontalAlignment.Stretch; VerticalAlignment = VerticalAlignment.Stretch; ClipToBounds = true;
            Visibility = Visibility.Collapsed;
            Loaded += (s,e) => { if (stopped) return; if (timer == null) { timer = new DispatcherTimer(DispatcherPriority.Background,Dispatcher) { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (sender,args) => Refresh(); } Refresh(); };
            Unloaded += (s,e) => { if (timer != null) timer.Stop(); };
            DataContextChanged += (s,e) => Refresh();
        }
        public void Refresh()
        {
            var game = GameContext ?? CoverGameContext.Resolve(DataContext);
            bool show = !stopped && enabled();
            if (timer != null) { if (show && IsLoaded) timer.Start(); else timer.Stop(); }
            var badge = show ? new CoverBadgeSnapshot(catalog(),history == null ? null : history(),selected == null ? null : selected()).Find(game) : null;
            if (badge != null) SizeToCover(badge.GameId);
            visual.Render(badge); Visibility = badge == null ? Visibility.Collapsed : Visibility.Visible;
        }
        private void SizeToCover(Guid gameId)
        {
            DependencyObject parent = VisualTreeHelper.GetParent(this);
            for (int depth = 0; parent != null && depth < 8; depth++,parent = VisualTreeHelper.GetParent(parent))
            {
                var grid = parent as Grid; if (grid == null) continue;
                var cover = CoverBadgeLayering.FindCover(grid,this,gameId);
                if (cover == null) continue;
                var bounds = CoverBadgePresenter.VisibleBounds(cover,grid);
                if (bounds.IsEmpty) continue;
                Width = bounds.Width; Height = bounds.Height;
                CoverBadgeLayering.Place(grid,cover,this);
                return;
            }
        }
        public override void GameContextChanged(Playnite.SDK.Models.Game oldContext, Playnite.SDK.Models.Game newContext) { Refresh(); }
        public void Shutdown() { stopped = true; if (timer != null) timer.Stop(); visual.Render(null); Visibility = Visibility.Collapsed; }
    }
}
