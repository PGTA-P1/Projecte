using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_
{
    // Small drawing helpers shared by the sector panel and the analysis window: the charts are plain WPF shapes on a
    // Canvas (no chart package), redrawn when the data, the size or the timeline time changes
    public static class Charts
    {
        // Same colours as the map legend (PlotOpenSectors.STATUS_COLORS, opaque)
        public static readonly Brush Green = Frozen(0x00, 0xAA, 0x00);
        public static readonly Brush Yellow = Frozen(0xF0, 0xC8, 0x00);
        public static readonly Brush Red = Frozen(0xDC, 0x14, 0x14);
        public static readonly Brush Closed = Frozen(0xE6, 0xE9, 0xED);
        public static readonly Brush Axis = Frozen(0x8A, 0x97, 0xA6);
        public static readonly Brush Text = Frozen(0x40, 0x40, 0x40);
        public static readonly Brush Cursor = Frozen(0x1F, 0x6F, 0xD1);

        // Software rendering for a window shown over the ArcGIS scene (the analysis and data files windows): with hardware
        // rendering their content sometimes stayed blank (white), although they worked. Light windows: the cost is nil
        public static void UseSoftwareRendering(System.Windows.Window window)
        {
            window.SourceInitialized += (s, e) =>
            {
                if (System.Windows.PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource source)
                    source.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            };
        }

        public static Brush StatusBrush(sbyte status)
        {
            return status == AirspaceTimeline.CLOSED ? Closed
                 : status == (sbyte)CapacityStatus.Red ? Red
                 : status == (sbyte)CapacityStatus.Yellow ? Yellow : Green;
        }

        public static Brush Frozen(byte r, byte g, byte b, byte a = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        public static Rectangle Rect(Canvas canvas, double x, double y, double w, double h, Brush fill)
        {
            var r = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill };
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            canvas.Children.Add(r);
            return r;
        }

        public static Line Line(Canvas canvas, double x1, double y1, double x2, double y2, Brush stroke, double thickness = 1, DoubleCollection dash = null)
        {
            var l = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness, StrokeDashArray = dash };
            canvas.Children.Add(l);
            return l;
        }

        public static TextBlock Label(Canvas canvas, string text, double x, double y, double size = 10, bool alignRight = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = Text };
            if (alignRight)
            {
                t.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                x -= t.DesiredSize.Width;
            }
            Canvas.SetLeft(t, x);
            Canvas.SetTop(t, y);
            canvas.Children.Add(t);
            return t;
        }

        // Hour labels along a time axis from 'from' to 'to' over [x0, x0 + width]: a tick every 'hours' hours, the day
        // added at midnight
        public static void TimeAxis(Canvas canvas, DateTime from, DateTime to, double x0, double width, double y, double hours)
        {
            double total = (to - from).TotalSeconds;
            if (total <= 0 || hours <= 0)
                return;
            DateTime t = from.Date.AddHours(Math.Ceiling((from - from.Date).TotalHours / hours) * hours);
            for (; t <= to; t = t.AddHours(hours))
            {
                double x = x0 + (t - from).TotalSeconds / total * width;
                Line(canvas, x, y, x, y + 3, Axis);
                Label(canvas, t.TimeOfDay == TimeSpan.Zero ? t.ToString("dd/MM") : t.ToString("HH:mm"), x - 14, y + 3, 9);
            }
        }
    }
}
