using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_
{
    public partial class AnalysisWindow : Window
    {
        public const int HEAT_MAP = 0, TRAFFIC = 1, OVERLOADS = 2, BUSIEST = 3;


        // Timeline line: thin and half transparent, so it marks the time without hiding the data
        private static readonly Brush SubtleCursor = Charts.Frozen(0x1F, 0x6F, 0xD1, 0x70);
        private static readonly Brush HoverGuide = Charts.Frozen(0x8A, 0x97, 0xA6, 0xA0);

        // Heat map: pixels per sample and per sector row; height of the time axis on top; width of the labels
        private const double ROW_H = 11, AXIS_H = 20, LABEL_W = 140;

        // Heat map: pixels per sample; Ctrl + mouse wheel changes it (1 to 8)
        private double _cellW = 2;

        // Traffic chart margins
        private const double LEFT = 40, RIGHT = 10, TOP = 8, BOTTOM = 20, GAP = 26;

        private AirspaceTimeline _tl;
        private List<int> _order = new List<int>();   // heat map rows: index into _tl.Sectors, in display order
        private DateTime _time;
        private Line _heatCursor;
        private Path _trafficCursor, _hoverLine;   // one segment per plot area of the traffic chart
        private Ellipse _trafficDot;                  // on the aircraft line at the timeline time; pop-up with the values
        private readonly ToolTip _dotTip = new ToolTip(); // updated in place, so it stays open while the time moves
        private double _aircraftTop, _aircraftBottom, _aircraftMax; // aircraft pane (for the dot's height)

        // Time (and, from the heat map, the sector clicked) the user wants the timeline to go to
        public event Action<DateTime, string> JumpRequested;

        // The traffic chart's line dragged (or clicked): the timeline follows, without bringing the main window forward
        public event Action<DateTime> ScrubRequested;

        public AnalysisWindow()
        {
            InitializeComponent();
            Charts.UseSoftwareRendering(this);
        }

        public void ShowTab(int index) => Tabs.SelectedIndex = index;

        public void SetTimeline(AirspaceTimeline timeline)
        {
            _tl = timeline;
            OrderRows();
            DrawHeatMap();
            DrawTraffic();
            FillOverloads();
            FillBusiest();
        }

        public void SetTime(DateTime t)
        {
            _time = t;
            MoveCursors();
        }

        private double XOfSample(double k) => k * _cellW;

        private void MoveCursors()
        {
            if (_tl == null)
                return;
            double k = (_time - _tl.Starts[0]).Ticks / (double)_tl.Step.Ticks;
            if (_heatCursor != null)
                _heatCursor.X1 = _heatCursor.X2 = XOfSample(k);
            if (_trafficCursor != null)
            {
                // Outside the range zoomed to: no line, no dot
                var (from, to) = TrafficRange();
                Visibility shown = _time >= from && _time <= to ? Visibility.Visible : Visibility.Collapsed;
                _trafficCursor.Visibility = shown;
                if (_trafficDot != null)
                    _trafficDot.Visibility = shown;
                MoveLine(_trafficCursor, TrafficX(_time));
            }

            // Dot on the aircraft line and the values at the timeline time (pop-up of the dot and the box under the chart)
            int s = _tl.IndexAt(_time);
            if (_trafficDot != null)
            {
                double y = _aircraftBottom - _tl.Aircraft[s] / _aircraftMax * (_aircraftBottom - _aircraftTop);
                Canvas.SetLeft(_trafficDot, TrafficX(_time) - _trafficDot.Width / 2);
                Canvas.SetTop(_trafficDot, y - _trafficDot.Height / 2);
                _dotTip.Content = TrafficText(s, "\n");
            }
            TrafficReadout.Text = "At the timeline time: " + TrafficText(s, " · ");
        }

        // Values of a sample; lines joined with separator
        private string TrafficText(int k, string separator)
        {
            var (ws, we) = _tl.Windows[k];
            int green = _tl.Open[k] - _tl.Near[k] - _tl.Over[k];
            var parts = new List<string>
            {
                $"{_tl.Starts[k]:dd/MM HH:mm} UTC (window {ws:HH:mm}-{we:HH:mm})",
                $"{_tl.Aircraft[k]} aircraft in the airspace",
                $"{_tl.Open[k]} sectors open: {green} under 80 %, {_tl.Near[k]} near the limit, {_tl.Over[k]} overloaded"
            };
            int top = _tl.MostLoaded(k);
            if (top >= 0)
                parts.Add($"most loaded: {_tl.Sectors[top]} {(_tl.Load(top, k) * 100).ToString("0", CultureInfo.InvariantCulture)} % " +
                          $"({_tl.Demand[top, k]} entries / {_tl.Capacity[top].Value.ToString("0.#", CultureInfo.InvariantCulture)})");
            return string.Join(separator, parts);
        }

        // ------------------------------------------------------------------------------------------------
        // DEMAND HEAT MAP: one row per sector, one column per sample (sliding step), each cell coloured as the map at that
        // time (green / yellow / red, grey = closed). A bitmap scaled without smoothing, labels on the left
        // ------------------------------------------------------------------------------------------------

        private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_tl == null)
                return;
            OrderRows();
            DrawHeatMap();
        }

        private void OrderRows()
        {
            IEnumerable<int> rows = Enumerable.Range(0, _tl.Sectors.Count);
            int Count(int r, Func<sbyte, bool> f) { int c = 0; for (int k = 0; k < _tl.Starts.Count; k++) if (f(_tl.Status[r, k])) c++; return c; }
            switch (SortBox.SelectedIndex)
            {
                case 1: rows = rows.OrderByDescending(r => Count(r, s => s == (sbyte)CapacityStatus.Red)).ThenBy(r => _tl.Sectors[r]); break;
                case 2: rows = rows.OrderByDescending(r => Count(r, s => s != AirspaceTimeline.CLOSED)).ThenBy(r => _tl.Sectors[r]); break;
            }
            _order = rows.ToList();
        }

        private void DrawHeatMap()
        {
            HeatLabels.Children.Clear();
            HeatCanvas.Children.Clear();
            _heatCursor = null;
            if (_tl == null)
                return;
            int n = _tl.Starts.Count, rows = _order.Count;

            var bmp = new WriteableBitmap(n, Math.Max(1, rows), 96, 96, PixelFormats.Bgr32, null);
            var pixels = new int[n * Math.Max(1, rows)];
            for (int i = 0; i < rows; i++)
                for (int k = 0; k < n; k++)
                    pixels[i * n + k] = Bgr(_tl.Status[_order[i], k]);
            bmp.WritePixels(new Int32Rect(0, 0, n, Math.Max(1, rows)), pixels, n * 4, 0);
            HeatImage.Source = bmp;
            HeatImage.Width = n * _cellW;
            HeatImage.Height = rows * ROW_H;
            Canvas.SetTop(HeatImage, AXIS_H);
            HeatCanvas.Children.Add(HeatImage);
            HeatCanvas.Width = n * _cellW + 10;
            HeatCanvas.Height = AXIS_H + rows * ROW_H + 4;

            // Thin white lines between ACCs (ACC order only)
            if (SortBox.SelectedIndex == 0)
                for (int i = 1; i < rows; i++)
                    if (_tl.SectorAcc[_order[i]] != _tl.SectorAcc[_order[i - 1]])
                        Charts.Line(HeatCanvas, 0, AXIS_H + i * ROW_H, n * _cellW, AXIS_H + i * ROW_H, Brushes.White, 2);

            Charts.TimeAxis(HeatCanvas, _tl.Starts[0], _tl.Starts[n - 1] + _tl.Step, 0, n * _cellW, 2, _cellW >= 6 ? 1 : 2);

            HeatLabels.Width = LABEL_W;
            HeatLabels.Height = HeatCanvas.Height;
            for (int i = 0; i < rows; i++)
            {
                var name = new TextBlock { Text = _tl.Sectors[_order[i]], FontSize = 9, Foreground = Charts.Text };
                var acc = new TextBlock { Text = _tl.SectorAcc[_order[i]], FontSize = 9, Foreground = Charts.Axis };
                Canvas.SetLeft(name, 4);
                Canvas.SetLeft(acc, 70);
                Canvas.SetTop(name, AXIS_H + i * ROW_H - 1);
                Canvas.SetTop(acc, AXIS_H + i * ROW_H - 1);
                HeatLabels.Children.Add(name);
                HeatLabels.Children.Add(acc);
            }

            _heatCursor = Charts.Line(HeatCanvas, 0, AXIS_H, 0, AXIS_H + rows * ROW_H, SubtleCursor, 2);
            _heatCursor.IsHitTestVisible = false;
            MoveCursors();
        }

        private static int Bgr(sbyte status)
        {
            return status == AirspaceTimeline.CLOSED ? 0xE6E9ED
                 : status == (sbyte)CapacityStatus.Red ? 0xDC1414
                 : status == (sbyte)CapacityStatus.Yellow ? 0xF0C800 : 0x00AA00;
        }

        // (row in _order, sample) under the mouse, or (-1, -1)
        private (int Row, int K) HeatCell(Point p)
        {
            if (_tl == null || p.Y < AXIS_H)
                return (-1, -1);
            int row = (int)((p.Y - AXIS_H) / ROW_H), k = (int)(p.X / _cellW);
            return row >= 0 && row < _order.Count && k >= 0 && k < _tl.Starts.Count ? (row, k) : (-1, -1);
        }

        private void HeatCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            var (row, k) = HeatCell(e.GetPosition(HeatCanvas));
            if (row < 0)
                return;
            int r = _order[row];
            var (ws, we) = _tl.Windows[k];
            string head = $"{_tl.Sectors[r]} ({_tl.SectorAcc[r]}), {_tl.Starts[k]:dd/MM HH:mm}: ";
            HeatReadout.Text = _tl.Status[r, k] == AirspaceTimeline.CLOSED ? head + "closed"
                             : head + $"window {ws:HH:mm}-{we:HH:mm}, {_tl.Demand[r, k]} entries, " + StatusText(_tl.Status[r, k]);
        }

        // Ctrl + wheel: wider or narrower time columns (the scroll bars follow); the plain wheel still scrolls
        private void HeatCanvas_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_tl == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                return;
            e.Handled = true;
            double[] widths = { 1, 2, 3, 4, 6, 8 };
            int i = Array.IndexOf(widths, _cellW);
            i = Math.Max(0, Math.Min(widths.Length - 1, i + (e.Delta > 0 ? 1 : -1)));
            if (widths[i] == _cellW)
                return;
            _cellW = widths[i];
            DrawHeatMap();
        }

        private void HeatCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var (row, k) = HeatCell(e.GetPosition(HeatCanvas));
            if (row >= 0)
                JumpRequested?.Invoke(_tl.Starts[k], _tl.Status[_order[row], k] == AirspaceTimeline.CLOSED ? null : _tl.Sectors[_order[row]]);
        }

        private static string StatusText(sbyte s) =>
            s == (sbyte)CapacityStatus.Red ? "overloaded (> 100 %)" : s == (sbyte)CapacityStatus.Yellow ? "near the limit (80-100 %)" : "under 80 %";

        // ------------------------------------------------------------------------------------------------
        // TRAFFIC OVER TIME: top, aircraft in the airspace (line); bottom, open sectors per sample stacked by status
        // (green, yellow near the limit, red overloaded)
        // ------------------------------------------------------------------------------------------------

        private double TrafficWidth => Math.Max(0, TrafficChart.ActualWidth - LEFT - RIGHT);

        // ZOOM: the time range shown (null = the whole simulation). Mouse wheel zooms around the mouse, Shift + wheel moves
        // it, a double-click shows everything again
        private DateTime? _viewFrom, _viewTo;
        private static readonly TimeSpan MIN_VIEW = TimeSpan.FromHours(1);

        private (DateTime From, DateTime To) TrafficRange()
        {
            DateTime from = _tl.Starts[0], to = _tl.Starts[_tl.Starts.Count - 1] + _tl.Step;
            return _viewFrom == null ? (from, to) : (_viewFrom.Value, _viewTo.Value);
        }

        private double TrafficX(DateTime t)
        {
            if (_tl == null)
                return 0;
            var (from, to) = TrafficRange();
            return LEFT + (t - from).TotalSeconds / (to - from).TotalSeconds * TrafficWidth;
        }

        private DateTime TrafficTime(double x)
        {
            var (from, to) = TrafficRange();
            double f = Math.Max(0, Math.Min(1, (x - LEFT) / Math.Max(1, TrafficWidth)));
            return from + TimeSpan.FromSeconds(f * (to - from).TotalSeconds);
        }

        private void TrafficChart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawTraffic();

        private void DrawTraffic()
        {
            TrafficChart.Children.Clear();
            _trafficCursor = _hoverLine = null;
            _trafficDot = null;
            if (_tl == null || TrafficWidth <= 0)
                return;
            int n = _tl.Starts.Count;
            double h = TrafficChart.ActualHeight - TOP - BOTTOM - GAP;
            if (h <= 0)
                return;
            double h1 = h * 0.5;
            double top1 = TOP + 14, bottom1 = TOP + h1, top2 = bottom1 + GAP + 14, bottom2 = TrafficChart.ActualHeight - BOTTOM;
            var (from, to) = TrafficRange();
            // Samples in the range shown (one more each side, so the line reaches the edges); scales from the whole day,
            // so zooming does not change the heights
            int k0 = Math.Max(0, _tl.IndexAt(from) - 1), k1 = Math.Min(n - 1, _tl.IndexAt(to) + 1);

            // Aircraft line
            int maxAircraft = Math.Max(1, _tl.Aircraft.Max());
            double max1 = maxAircraft * 1.1;
            _aircraftTop = top1;
            _aircraftBottom = bottom1;
            _aircraftMax = max1;
            var plot = new Canvas { ClipToBounds = true, Width = TrafficWidth, Height = TrafficChart.ActualHeight, IsHitTestVisible = false };
            Canvas.SetLeft(plot, LEFT);
            TrafficChart.Children.Add(plot);
            var line = new Polyline { Stroke = Charts.Cursor, StrokeThickness = 1.5 };
            for (int k = k0; k <= k1; k++)
                line.Points.Add(new Point(TrafficX(_tl.Starts[k]) - LEFT, bottom1 - _tl.Aircraft[k] / max1 * (bottom1 - top1)));
            plot.Children.Add(line);
            Axes(top1, bottom1, max1, "Aircraft in the airspace");

            // Open sectors stacked by status
            int maxOpen = Math.Max(1, _tl.Open.Max());
            double max2 = maxOpen * 1.1, scale = (bottom2 - top2) / max2;
            for (int k = k0; k <= k1; k++)
            {
                double x0 = TrafficX(_tl.Starts[k]) - LEFT, w = Math.Max(1, TrafficX(_tl.Starts[k] + _tl.Step) - LEFT - x0 - 0.2);
                double green = _tl.Open[k] - _tl.Near[k] - _tl.Over[k];
                double y = bottom2;
                foreach (var (count, brush) in new[] { (green, Charts.Green), ((double)_tl.Near[k], Charts.Yellow), ((double)_tl.Over[k], Charts.Red) })
                {
                    if (count <= 0) continue;
                    Charts.Rect(plot, x0, y - count * scale, w, count * scale, brush);
                    y -= count * scale;
                }
            }
            Axes(top2, bottom2, max2, "Open sectors: under 80 % (green), near the limit (yellow), overloaded (red)");
            double hours = (to - from).TotalHours;
            Charts.TimeAxis(TrafficChart, from, to, LEFT, TrafficWidth, bottom2, hours > 24 ? 3 : hours > 8 ? 1 : 0.5);

            // Timeline line (subtle, over the plot areas only, grab it to move the time), mouse guide (faint, dashed),
            // dot with pop-up
            _trafficCursor = PaneLine((top1, bottom1), (top2, bottom2), SubtleCursor, null);
            _hoverLine = PaneLine((top1, bottom1), (top2, bottom2), HoverGuide, new DoubleCollection { 3, 3 });
            _hoverLine.Visibility = Visibility.Collapsed;
            _trafficDot = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, Stroke = Charts.Cursor, StrokeThickness = 3, Cursor = Cursors.SizeWE, ToolTip = _dotTip };
            ToolTipService.SetInitialShowDelay(_trafficDot, 0);
            ToolTipService.SetShowDuration(_trafficDot, 60000);
            TrafficChart.Children.Add(_trafficDot);
            MoveCursors();

            int peakA = Array.IndexOf(_tl.Aircraft, _tl.Aircraft.Max()), peakO = Array.IndexOf(_tl.Over, _tl.Over.Max());
            int withOverload = _tl.Over.Count(o => o > 0);
            TrafficSummary.Text = $"Peak {_tl.Aircraft[peakA]} aircraft at {_tl.Starts[peakA]:dd/MM HH:mm} UTC. " +
                                  $"Most sectors overloaded at once: {_tl.Over[peakO]} at {_tl.Starts[peakO]:dd/MM HH:mm}. " +
                                  $"Some sector overloaded during {Duration(withOverload * _tl.Step)} " +
                                  $"(window {_tl.Window.TotalMinutes:0} min, step {_tl.Step.TotalMinutes:0} min). " +
                                  "Drag the line (or click) to move the time; wheel zooms, Shift + wheel moves, double-click shows all.";
        }

        // Vertical line at x = 0 over the given plot areas only, moved sideways with MoveLine
        private Path PaneLine((double Top, double Bottom) a, (double Top, double Bottom) b, Brush stroke, DoubleCollection dash)
        {
            var geometry = new GeometryGroup();
            geometry.Children.Add(new LineGeometry(new Point(0, a.Top), new Point(0, a.Bottom)));
            geometry.Children.Add(new LineGeometry(new Point(0, b.Top), new Point(0, b.Bottom)));
            var path = new Path { Data = geometry, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = dash, IsHitTestVisible = false,
                                  RenderTransform = new TranslateTransform() };
            TrafficChart.Children.Add(path);
            return path;
        }

        private static void MoveLine(Path line, double x) => ((TranslateTransform)line.RenderTransform).X = x;

        private void Axes(double top, double bottom, double max, string title)
        {
            Charts.Line(TrafficChart, LEFT, bottom, LEFT + TrafficWidth, bottom, Charts.Axis);
            Charts.Line(TrafficChart, LEFT, top, LEFT, bottom, Charts.Axis);
            Charts.Label(TrafficChart, "0", LEFT - 4, bottom - 7, 9, alignRight: true);
            Charts.Label(TrafficChart, Math.Round(max / 1.1).ToString(CultureInfo.InvariantCulture), LEFT - 4, bottom - (bottom - top) / 1.1 - 7, 9, alignRight: true);
            Charts.Line(TrafficChart, LEFT, bottom - (bottom - top) / 1.1, LEFT + TrafficWidth, bottom - (bottom - top) / 1.1, Charts.Closed);
            Charts.Label(TrafficChart, title, LEFT + 4, top - 16, 11);
        }

        private bool InPlot(Point p) => _tl != null && TrafficWidth > 0 && p.X >= LEFT && p.X <= LEFT + TrafficWidth;

        // DRAG: pressing on the chart moves the timeline there, and it follows the mouse until the button is released
        private bool _dragging;

        private void TrafficChart_MouseMove(object sender, MouseEventArgs e)
        {
            if (_hoverLine == null)
                return;
            Point p = e.GetPosition(TrafficChart);
            if (_dragging && e.LeftButton != MouseButtonState.Pressed)
                _dragging = false;  // released outside (capture lost): stop following the mouse
            if (_dragging)
            {
                _hoverLine.Visibility = Visibility.Collapsed;
                ScrubRequested?.Invoke(TrafficTime(p.X));
                return;
            }
            bool inside = InPlot(p);
            _hoverLine.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
            MoveLine(_hoverLine, p.X);
            // Near the timeline line: it can be grabbed
            TrafficChart.Cursor = inside && Math.Abs(p.X - TrafficX(_time)) < 6 ? Cursors.SizeWE : Cursors.Hand;
            int k = inside ? _tl.IndexAt(TrafficTime(p.X)) : -1;
            TrafficChart.ToolTip = k < 0 ? null : $"{_tl.Starts[k]:dd/MM HH:mm}: {_tl.Aircraft[k]} aircraft, {_tl.Over[k]} overloaded. Click or drag to go there.";
        }

        private void TrafficChart_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_hoverLine != null)
                _hoverLine.Visibility = Visibility.Collapsed;
        }

        private void TrafficChart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(TrafficChart);
            if (e.ClickCount == 2)
            {
                _viewFrom = _viewTo = null;   // double-click: the whole simulation again
                DrawTraffic();
                return;
            }
            if (!InPlot(p))
                return;
            _dragging = true;
            TrafficChart.CaptureMouse();
            ScrubRequested?.Invoke(TrafficTime(p.X));
        }

        private void TrafficChart_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            TrafficChart.ReleaseMouseCapture();
        }

        private void TrafficChart_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void TrafficChart_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_tl == null)
                return;
            e.Handled = true;
            DateTime first = _tl.Starts[0], last = _tl.Starts[_tl.Starts.Count - 1] + _tl.Step;
            var (from, to) = TrafficRange();
            TimeSpan span = to - from;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                // Move by a tenth of the range, wheel down = later
                TimeSpan shift = TimeSpan.FromTicks(span.Ticks / 10 * (e.Delta < 0 ? 1 : -1));
                from += shift;
                to += shift;
                if (from < first) { to += first - from; from = first; }
                if (to > last) { from -= to - last; to = last; }
            }
            else
            {
                // Zoom around the time under the mouse, wheel up = closer
                DateTime at = TrafficTime(e.GetPosition(TrafficChart).X);
                double factor = e.Delta > 0 ? 0.75 : 1 / 0.75;
                TimeSpan newSpan = TimeSpan.FromTicks((long)(span.Ticks * factor));
                if (newSpan < MIN_VIEW) newSpan = MIN_VIEW;
                if (newSpan >= last - first) { _viewFrom = _viewTo = null; DrawTraffic(); return; }
                double f = (at - from).TotalSeconds / span.TotalSeconds;
                from = at - TimeSpan.FromSeconds(f * newSpan.TotalSeconds);
                to = from + newSpan;
                if (from < first) { to += first - from; from = first; }
                if (to > last) { from -= to - last; to = last; }
            }
            _viewFrom = from;
            _viewTo = to;
            DrawTraffic();
        }

        // ------------------------------------------------------------------------------------------------
        // OVERLOAD REPORT: every overload episode (consecutive red samples of a sector, AirspaceTimeline.Overloads), most
        // severe first; export to Exports/overloads.csv with the parameters on top
        // ------------------------------------------------------------------------------------------------

        private List<AirspaceTimeline.Overload> _overloads = new List<AirspaceTimeline.Overload>();

        private void FillOverloads()
        {
            _overloads = _tl.Overloads().OrderByDescending(o => o.PeakLoad).ThenByDescending(o => o.Duration).ToList();
            OverloadGrid.ItemsSource = _overloads;
            TimeSpan total = TimeSpan.FromTicks(_overloads.Sum(o => o.Duration.Ticks));
            int sectors = _overloads.Select(o => o.Sector).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            OverloadSummary.Text = $"{_overloads.Count} overloads in {sectors} sectors, {Duration(total)} in total " +
                                   $"(demand above capacity in the window shown; window {_tl.Window.TotalMinutes:0} min, step {_tl.Step.TotalMinutes:0} min). " +
                                   "Double-click a row to go there. Results > Export overloads saves them.";
        }

        private void OverloadGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (OverloadGrid.SelectedItem is AirspaceTimeline.Overload o)
                JumpRequested?.Invoke(o.From, o.Sector);
        }

        // ------------------------------------------------------------------------------------------------
        // BUSIEST SECTORS: one row per sector of the timeline, sorted by time overloaded (then entries)
        // ------------------------------------------------------------------------------------------------

        public class BusyRow
        {
            public string Sector { get; set; }
            public string Acc { get; set; }
            public int Entries { get; set; }
            public double OpenMinutes { get; set; }
            public double AverageMinutes { get; set; }
            public double PeakLoad { get; set; }      // -1 = no limit
            public double NearMinutes { get; set; }
            public double OverMinutes { get; set; }
            public double OverBar { get; set; }        // pixels
            public DateTime PeakTime { get; set; }
            public string OpenText => Duration(TimeSpan.FromMinutes(OpenMinutes));
            public string AverageText => $"{(int)AverageMinutes} min {(AverageMinutes - (int)AverageMinutes) * 60:00} s";
            public string PeakText => PeakLoad < 0 ? "no limit" : (PeakLoad * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
            public string NearText => Duration(TimeSpan.FromMinutes(NearMinutes));
            public string OverText => Duration(TimeSpan.FromMinutes(OverMinutes));
        }

        private void FillBusiest()
        {
            double step = _tl.Step.TotalMinutes;
            var rows = new List<BusyRow>();
            for (int i = 0; i < _tl.Sectors.Count; i++)
            {
                int peak = -1;
                for (int k = 0; k < _tl.Starts.Count; k++)
                    if (_tl.Load(i, k) >= 0 && (peak < 0 || _tl.Load(i, k) > _tl.Load(i, peak)))
                        peak = k;
                rows.Add(new BusyRow
                {
                    Sector = _tl.Sectors[i], Acc = _tl.SectorAcc[i], Entries = _tl.EntryCount[i], AverageMinutes = _tl.AverageMinutes[i],
                    OpenMinutes = _tl.Count(i, s => s != AirspaceTimeline.CLOSED) * step,
                    NearMinutes = _tl.Count(i, s => s == (sbyte)CapacityStatus.Yellow) * step,
                    OverMinutes = _tl.Count(i, s => s == (sbyte)CapacityStatus.Red) * step,
                    PeakLoad = peak < 0 ? -1 : _tl.Load(i, peak),
                    PeakTime = peak < 0 ? _tl.Starts[0] : _tl.Starts[peak]
                });
            }
            double maxOver = Math.Max(1, rows.Max(r => r.OverMinutes));
            foreach (BusyRow r in rows)
                r.OverBar = r.OverMinutes / maxOver * 120;
            BusiestGrid.ItemsSource = rows.OrderByDescending(r => r.OverMinutes).ThenByDescending(r => r.Entries).ToList();
        }

        private void BusiestGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (BusiestGrid.SelectedItem is BusyRow r)
                JumpRequested?.Invoke(r.PeakTime, r.Sector);
        }

        public static string Duration(TimeSpan d)
        {
            return d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} min" : $"{d.Minutes} min";
        }
    }
}
