using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace PlayniteUpdateStatus.UI
{
    internal sealed class CoverBadgeArtwork : FrameworkElement
    {
        private static readonly Brush Grey = Frozen(Color.FromRgb(191,192,194));
        private static readonly Typeface Semibold = new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.SemiBold,FontStretches.Normal);
        private static readonly Typeface Bold = new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeights.Bold,FontStretches.Normal);
        private CoverBadgeInfo badge;
        private double screenScale = 1;
        private static Brush Frozen(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
        public CoverBadgeArtwork()
        {
            IsHitTestVisible = false; Focusable = false;
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = .8 };
        }
        public void SetBadge(CoverBadgeInfo next)
        {
            if (badge == null && next == null || badge != null && next != null && badge.CoverText == next.CoverText && badge.CoverPercent == next.CoverPercent) return;
            badge = next; InvalidateVisual();
        }
        public void SetScreenScale(double next)
        {
            if (Math.Abs(screenScale-next) < .0001) return;
            screenScale = next; InvalidateVisual();
        }
        protected override void OnRender(DrawingContext drawing)
        {
            base.OnRender(drawing);
            if (badge == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            double width = ActualWidth, height = ActualHeight;
            double scale = Math.Min(width,height)/300;
            var center = new Point(width/2,height/2);
            drawing.PushOpacity(.8);
            if (badge.CoverText == "Downloading" || badge.CoverText == "Paused")
            {
                DrawText(drawing,badge.CoverText,36*scale,center.X,16*scale,false);
                if (badge.CoverText == "Paused")
                {
                    double barWidth = 32*scale, gap = 25*scale, barHeight = 92*scale;
                    double left = center.X-(2*barWidth+gap)/2;
                    drawing.DrawRoundedRectangle(Grey,null,new Rect(left,center.Y-barHeight/2,barWidth,barHeight),2*scale,2*scale);
                    drawing.DrawRoundedRectangle(Grey,null,new Rect(left+barWidth+gap,center.Y-barHeight/2,barWidth,barHeight),2*scale,2*scale);
                }
                else if (badge.CoverPercent.HasValue)
                {
                    string value = badge.CoverPercent.Value.ToString(CultureInfo.CurrentCulture)+"%";
                    var text = Text(value,96*scale,true);
                    if (text.Width > width*.9) text = Text(value,96*scale*(width*.9/text.Width),true);
                    drawing.DrawText(text,new Point(center.X-text.Width/2,center.Y-text.Height/2));
                }
            }
            else if (badge.CoverText != "Update Required")
            {
                int split = badge.CoverText.LastIndexOf(' ');
                double line = 36*scale*1.15, gap = 4*scale;
                if (split > 0)
                {
                    DrawText(drawing,badge.CoverText.Substring(0,split),36*scale,center.X,center.Y-(2*line+gap)/2,false);
                    DrawText(drawing,badge.CoverText.Substring(split+1),36*scale,center.X,center.Y+gap/2,false);
                }
                else DrawText(drawing,badge.CoverText,36*scale,center.X,center.Y-line/2,false);
            }
            else
            {
                double stroke = 21*scale, radius = (276*scale-stroke)/2;
                var pen = new Pen(Grey,stroke) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }; pen.Freeze();
                Arc(drawing,pen,center,radius,45,52); Arc(drawing,pen,center,radius,55,62); Arc(drawing,pen,center,radius,65,355);
                var end = At(center,radius,355);
                double radians = 355*Math.PI/180;
                var tangent = new Vector(Math.Cos(radians),Math.Sin(radians));
                var normal = new Vector(Math.Sin(radians),-Math.Cos(radians));
                var baseCenter = end-tangent*(3*scale);
                var head = new StreamGeometry();
                using (var shape = head.Open())
                {
                    shape.BeginFigure(baseCenter+normal*(20*scale),true,true);
                    shape.LineTo(baseCenter+tangent*(42*scale),true,false);
                    shape.LineTo(baseCenter-normal*(20*scale),true,false);
                }
                head.Freeze(); drawing.DrawGeometry(Grey,null,head);
                double line = 36*scale*1.15, gap = 6*scale;
                DrawText(drawing,"Update",36*scale,center.X,center.Y-(2*line+gap)/2,false);
                DrawText(drawing,"Required",36*scale,center.X,center.Y+gap/2,false);
            }
            drawing.Pop();
        }
        private FormattedText Text(string value, double size, bool bold)
        {
            size = Math.Max(1,size);
            var text = new FormattedText(value,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,bold ? Bold : Semibold,size,Grey,VisualTreeHelper.GetDpi(this).PixelsPerDip);
            text.LineHeight = size*(bold ? 1 : 1.15); return text;
        }
        private void DrawText(DrawingContext drawing, string value, double size, double centerX, double top, bool bold)
        {
            size = Math.Max(size,10/screenScale);
            var text = Text(value,size,bold);
            if (text.Width > ActualWidth*.82) text = Text(value,size*(ActualWidth*.82/text.Width),bold);
            drawing.DrawText(text,new Point(centerX-text.Width/2,top));
        }
        private static Point At(Point center, double radius, double angle)
        { double radians = angle*Math.PI/180; return new Point(center.X+radius*Math.Sin(radians),center.Y-radius*Math.Cos(radians)); }
        private static void Arc(DrawingContext drawing, Pen pen, Point center, double radius, double start, double end)
        {
            var arc = new StreamGeometry();
            using (var shape = arc.Open())
            {
                shape.BeginFigure(At(center,radius,start),false,false);
                shape.ArcTo(At(center,radius,end),new Size(radius,radius),0,end-start > 180,SweepDirection.Clockwise,true,false);
            }
            arc.Freeze(); drawing.DrawGeometry(null,pen,arc);
        }
    }
}
