using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace PlayniteUpdateStatus.UI
{
    // Drawn in reserved title space. It never takes focus or intercepts row input.
    public sealed class LibraryRowBadgeVisual : FrameworkElement
    {
        private CoverBadgeInfo badge;
        private double fontSize = 13;
        private double availableWidth;
        private bool compact;
        private FormattedText fullText;
        private FormattedText percentText;
        private double fullWidth;
        private double percentWidth;
        private double pixelsPerDip;
        private CultureInfo textCulture;
        private string accessibleName = "";
        private static readonly Typeface BadgeTypeface = new Typeface("Segoe UI");
        private static readonly Brush Ink = new SolidColorBrush(Color.FromArgb(204,191,192,194));
        private static readonly Brush DarkInk = new SolidColorBrush(Color.FromArgb(204,86,87,89));
        private Brush ink = Ink;
        static LibraryRowBadgeVisual() { Ink.Freeze(); DarkInk.Freeze(); }
        public LibraryRowBadgeVisual() { IsHitTestVisible = false; Focusable = false; ClipToBounds = true; FlowDirection = FlowDirection.LeftToRight; }
        public double Configure(CoverBadgeInfo value, double titleFontSize, double available)
        { return Configure(value,titleFontSize,available,null); }
        public double Configure(CoverBadgeInfo value, double titleFontSize, double available, Brush titleForeground)
        {
            var foreground = titleForeground as SolidColorBrush;
            Brush nextInk = foreground != null && foreground.Color.R*.2126+foreground.Color.G*.7152+foreground.Color.B*.0722 < 128 ? DarkInk : Ink;
            double nextFont = Math.Max(11,Math.Min(24,titleFontSize * .8));
            double nextWidth = Math.Max(0,available);
            double nextDpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var nextCulture = CultureInfo.CurrentUICulture;
            bool contentChanged = (badge == null) != (value == null) ||
                badge != null && value != null && (badge.CoverText != value.CoverText || badge.CoverPercent != value.CoverPercent);
            bool textChanged = contentChanged || fontSize != nextFont || ink != nextInk ||
                pixelsPerDip != nextDpi || !Equals(textCulture,nextCulture);
            bool changed = textChanged || availableWidth != nextWidth;
            ink = nextInk;
            badge = value; fontSize = nextFont;
            availableWidth = nextWidth; pixelsPerDip = nextDpi; textCulture = nextCulture;
            if (textChanged) RefreshText();
            compact = fullWidth > availableWidth;
            if (changed) InvalidateVisual();
            return Math.Min(availableWidth,compact ? SymbolSize + (percentText != null ? percentWidth + 9 : 0) : fullWidth);
        }
        private double SymbolSize { get { return fontSize * 1.35; } }
        private FormattedText Text(string text)
        { return new FormattedText(text,textCulture,FlowDirection.LeftToRight,BadgeTypeface,fontSize,ink,pixelsPerDip); }
        private void RefreshText()
        {
            string percent = badge != null && badge.CoverPercent.HasValue
                ? badge.CoverPercent.Value.ToString(CultureInfo.InvariantCulture) + "%" : "";
            string label = badge == null ? "" : badge.CoverText + (percent.Length == 0 ? "" : " " + percent);
            fullText = badge == null ? null : Text(label);
            percentText = percent.Length == 0 ? null : Text(percent);
            // Rendering applies an ellipsis constraint to these objects. Keep
            // natural widths separately so resizing can restore the full label.
            fullWidth = fullText == null ? 0 : SymbolSize + 7 + fullText.Width + 2;
            percentWidth = percentText == null ? 0 : percentText.Width;
            if (accessibleName != label)
            {
                accessibleName = label;
                AutomationProperties.SetName(this,label);
            }
        }
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); if (badge == null || ActualWidth < SymbolSize || ActualHeight < 14) return;
            double size = Math.Min(SymbolSize,ActualHeight - 4), top = (ActualHeight-size)/2;
            if (badge.CoverText == "Paused")
            {
                double bar = size * .28;
                dc.DrawRoundedRectangle(ink,null,new Rect(1,top,bar,size),1,1);
                dc.DrawRoundedRectangle(ink,null,new Rect(1+size*.65,top,bar,size),1,1);
            }
            else
            {
                var center = new Point(size/2,top+size/2); double radius = size*.37;
                var start = new Point(center.X+radius,center.Y);
                var end = new Point(center.X,center.Y-radius);
                var arc = new StreamGeometry();
                using (var g = arc.Open()) { g.BeginFigure(start,false,false); g.ArcTo(end,new Size(radius,radius),0,true,SweepDirection.Clockwise,true,false); }
                dc.DrawGeometry(null,new Pen(ink,Math.Max(1.6,size*.12)),arc);
                var head = new StreamGeometry();
                using (var g = head.Open()) { g.BeginFigure(new Point(end.X+size*.25,end.Y),true,true); g.LineTo(new Point(end.X-size*.08,end.Y-size*.2),true,false); g.LineTo(new Point(end.X-size*.08,end.Y+size*.2),true,false); }
                dc.DrawGeometry(ink,null,head);
            }
            var text = compact ? percentText : fullText;
            if (text == null) return;
            double x = SymbolSize + 7;
            if (ActualWidth-x < 1) return;
            text.MaxTextWidth = ActualWidth-x; text.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(text,new Point(x,(ActualHeight-text.Height)/2));
        }
    }
}
