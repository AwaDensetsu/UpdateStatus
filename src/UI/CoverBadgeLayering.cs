using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlayniteUpdateStatus.UI
{
    internal static class CoverBadgeLayering
    {
        // Use the cover's direct child branch as the anchor. Equal Z-index and
        // immediate adjacency keep the artwork above the image but below later
        // theme actions. Only the plugin element is moved or assigned a Z-index.
        public static void Place(Grid parent, FrameworkElement cover, UIElement overlay)
        {
            int overlayIndex = parent.Children.IndexOf(overlay);
            if (overlayIndex < 0) return;
            DependencyObject branch = cover;
            for (int depth = 0; branch != null && depth < 32; depth++)
            {
                var ancestor = VisualTreeHelper.GetParent(branch);
                if (ancestor == parent) break;
                branch = ancestor;
            }
            var anchor = branch as UIElement;
            if (anchor == null || anchor == overlay) return;
            int coverIndex = parent.Children.IndexOf(anchor);
            if (coverIndex < 0) return;
            int z = Panel.GetZIndex(anchor);
            if (Panel.GetZIndex(overlay) != z) overlay.SetCurrentValue(Panel.ZIndexProperty,z);
            if (overlayIndex == coverIndex + 1) return;
            parent.Children.RemoveAt(overlayIndex);
            if (overlayIndex < coverIndex) coverIndex--;
            parent.Children.Insert(coverIndex + 1,overlay);
        }

        // SDK controls can sit beside a Border/ContentPresenter that wraps the
        // cover. Search those siblings with a bounded walk, without moving any
        // theme-owned wrapper. A known different game's cover is never an anchor.
        public static FrameworkElement FindCover(Grid parent, UIElement overlay, Guid gameId)
        {
            var pending = new Stack<DependencyObject>();
            for (int i = parent.Children.Count - 1; i >= 0; i--) pending.Push(parent.Children[i]);
            int budget = 128;
            while (pending.Count > 0 && budget-- > 0)
            {
                var node = pending.Pop();
                if (node == overlay || node is CoverBadgeControl || node is CoverBadgeVisual || node is LibraryRowBadgeVisual) continue;
                var element = node as FrameworkElement;
                if (element != null && (!element.IsVisible || element.Opacity == 0)) continue;
                if (element != null && CoverBadgePresenter.IsCover(element))
                {
                    var game = CoverGameContext.Resolve(element.DataContext);
                    if ((game == null || game.Id == gameId) && !CoverBadgePresenter.VisibleBounds(element,parent).IsEmpty) return element;
                }
                for (int i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node,i));
            }
            return null;
        }
    }
}
