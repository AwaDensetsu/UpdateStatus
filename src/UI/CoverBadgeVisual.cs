using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;

namespace PlayniteUpdateStatus.UI
{
    public sealed class CoverBadgeVisual : Border
    {
        private readonly CoverBadgeArtwork artwork = new CoverBadgeArtwork();
        private static readonly Brush Surface = new SolidColorBrush(Color.FromArgb(133,0,0,0));
        static CoverBadgeVisual() { Surface.Freeze(); }
        public CoverBadgeVisual()
        {
            Style = new Style(typeof(Border)); Background = Surface; CornerRadius = new CornerRadius(0);
            BorderThickness = new Thickness(0); Padding = new Thickness(0); IsHitTestVisible = false; Focusable = false; ClipToBounds = true;
            Child = artwork;
        }
        public void Render(CoverBadgeInfo badge)
        {
            Visibility = badge == null ? Visibility.Collapsed : Visibility.Visible;
            artwork.SetBadge(badge);
            AutomationProperties.SetName(this,badge == null ? "" : badge.CoverText + (badge.CoverPercent.HasValue ? " — " + badge.CoverPercent.Value + "%" : ""));
        }
        public void SetScale(double scale)
        { artwork.SetScreenScale(scale); }
    }
}
