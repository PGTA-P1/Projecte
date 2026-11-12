using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_
{
    // What the sector panel shows that does not change with the timeline time (MainWindow.BuildSectorInfo)
    public class SectorInfo
    {
        public string Name;
        public string Kind;                // e.g. "Collapsed sector: LECPGXX + LECPOXX"
        public string Info;                // ACC, flight levels, capacity
        public double? CapacityPerWindow;  // null = no limit
        public List<(DateTime Start, DateTime End)> OpenPeriods = new List<(DateTime, DateTime)>();
        public AirspaceTimeline Timeline;  // null while it is being built
        public int Row = -1;               // row of the sector in Timeline
        public List<(string Label, string Value, string Tip)> Figures = new List<(string, string, string)>();

        // Flows: where its flights come from and go to (previous / next open sector, or outside), most common first
        public List<(string Name, int Count)> From = new List<(string, int)>();
        public List<(string Name, int Count)> To = new List<(string, int)>();

        // Flight-level profile: aircraft-minutes inside per band of LEVEL_BAND flight levels (band floor), split by
        // climbing / level / descending
        public const int LEVEL_BAND = 20;
        public List<(int Fl, double Climb, double Level, double Descend)> Levels = new List<(int, double, double, double)>();

        // Aircraft inside the sector at each sample of Timeline (same index), drawn over the demand bars
        public int[] AircraftInside;

        // Every flow (not only the top 5), for the Excel export
        public List<(string Name, int Count)> FromAll = new List<(string, int)>();
        public List<(string Name, int Count)> ToAll = new List<(string, int)>();
    }

    // What it shows at the timeline time (MainWindow.SectorNowAt)
    public class SectorNow
    {
        public DateTime Time;
        public string Title;               // time, configuration, window (or closed)
        public string Load;                // "89 %", "-" (no limit) or "closed"
        public sbyte Status;               // AirspaceTimeline.CLOSED or (sbyte)CapacityStatus
        public string Demand;              // "11 entries / 12.3 per window"
        public List<string> Inside = new List<string>();
        public List<string> Next = new List<string>();
    }

    public partial class SectorPanel : UserControl
    {
        // Chart margins: y labels on the left, time labels at the bottom
        private const double LEFT = 26, BOTTOM = 16, TOP = 4, RIGHT = 4;

        private SectorInfo _info;
        private DateTime _from, _to;        // time range of the chart
        private Line _cursor;
        private DateTime _time;
        private string _insideKey, _nextKey;

        public event EventHandler CloseRequested;
        public event Action<DateTime> TimeRequested;
        public event EventHandler ExportRequested;

        public SectorPanel()
        {
            InitializeComponent();
        }

        public string Sector => _info?.Name;
        public SectorInfo Info => _info;
        public bool PauseOnClose => PauseToggle.IsChecked == true;

        public void SetSector(SectorInfo info)
        {
            if (_info?.Name != info.Name)
                _viewFrom = _viewTo = null; // another sector: no zoom
            _info = info;
            NameText.Text = info.Name;
            KindText.Text = info.Kind;
            InfoText.Text = info.Info;

            Figures.Children.Clear();
            Figures.RowDefinitions.Clear();
            foreach (var (label, value, tip) in info.Figures)
            {
                int row = Figures.RowDefinitions.Count;
                Figures.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Margin = new Thickness(0, 1, 8, 1), ToolTip = tip, TextWrapping = TextWrapping.Wrap };
                var v = new TextBlock { Text = value, Margin = new Thickness(0, 1, 0, 1), FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right };
                Grid.SetRow(l, row);
                Grid.SetRow(v, row);
                Grid.SetColumn(v, 1);
                Figures.Children.Add(l);
                Figures.Children.Add(v);
            }
            _insideKey = _nextKey = null;
            FillFlows(FromList, info.From);
            FillFlows(ToList, info.To);
            DrawChart();
            DrawLevels();
        }

        // Top flows as name, bar (share of the most common) and count
        private static void FillFlows(StackPanel list, List<(string Name, int Count)> flows)
        {
            list.Children.Clear();
            if (flows.Count == 0)
            {
                list.Children.Add(new TextBlock { Text = "None", FontSize = 11, Foreground = Charts.Axis });
                return;
            }
            int max = flows.Max(f => f.Count);
            foreach (var (name, count) in flows)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var bar = new Rectangle { Height = 14, HorizontalAlignment = HorizontalAlignment.Left, Fill = Charts.Frozen(0xD6, 0xE4, 0xF5),
                                          Width = 1 + 100.0 * count / max };
                var label = new TextBlock { Text = name, FontSize = 11, Margin = new Thickness(3, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                var value = new TextBlock { Text = count.ToString(CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.SemiBold };
                Grid.SetColumn(value, 1);
                row.Children.Add(bar);
                row.Children.Add(label);
                row.Children.Add(value);
                list.Children.Add(row);
            }
        }

        // Flight-level profile: one row per band (highest on top), a stacked bar climbing / level / descending
        private static readonly Brush Climb = Charts.Frozen(0x2E, 0xA0, 0x43), LevelFlight = Charts.Frozen(0x1F, 0x6F, 0xD1), Descend = Charts.Frozen(0xF0, 0x8C, 0x00);

        private void DrawLevels()
        {
            LevelChart.Children.Clear();
            if (_info == null || _info.Levels.Count == 0)
            {
                LevelChart.Height = 16;
                Charts.Label(LevelChart, "No traffic", 0, 0, 11);
                return;
            }
            const double ROW = 14, LABEL = 46;
            LevelChart.Height = _info.Levels.Count * ROW + 18;
            double width = Math.Max(10, LevelChart.ActualWidth - LABEL - 40);
            double max = _info.Levels.Max(l => l.Climb + l.Level + l.Descend);
            int row = 0;
            foreach (var (fl, climb, level, descend) in _info.Levels.OrderByDescending(l => l.Fl))
            {
                double y = row++ * ROW;
                Charts.Label(LevelChart, $"FL{fl:000}", 0, y, 10);
                double x = LABEL;
                foreach (var (minutes, brush) in new[] { (climb, Climb), (level, LevelFlight), (descend, Descend) })
                {
                    double w = minutes / max * width;
                    if (w > 0)
                        Charts.Rect(LevelChart, x, y + 2, w, ROW - 4, brush);
                    x += w;
                }
                Charts.Label(LevelChart, $"{climb + level + descend:0} min", x + 4, y, 9);
            }
            double ly = row * ROW + 2, lx = LABEL;
            foreach (var (text, brush) in new[] { ("climbing", Climb), ("level", LevelFlight), ("descending", Descend) })
            {
                Charts.Rect(LevelChart, lx, ly + 3, 8, 8, brush);
                Charts.Label(LevelChart, text, lx + 11, ly, 10);
                lx += 72;
            }
        }

        private void LevelChart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
                DrawLevels();
        }

        public void SetNow(SectorNow now)
        {
            _time = now.Time;
            NowTitle.Text = now.Title;
            NowLoad.Text = now.Load;
            NowPill.Background = now.Status == AirspaceTimeline.CLOSED ? Charts.Closed : Charts.StatusBrush(now.Status);
            NowLoad.Foreground = now.Status == (sbyte)CapacityStatus.Red || now.Status == (sbyte)CapacityStatus.Green ? Brushes.White : Charts.Text;
            NowDemand.Text = now.Demand;

            // Lists replaced only when they change (they are refreshed at every timeline update)
            string inside = string.Join("\n", now.Inside);
            if (inside != _insideKey)
            {
                _insideKey = inside;
                InsideList.ItemsSource = now.Inside;
                InsideTitle.Text = $"Aircraft inside now ({now.Inside.Count})";
                InsideEmpty.Visibility = now.Inside.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            string next = string.Join("\n", now.Next);
            if (next != _nextKey)
            {
                _nextKey = next;
                NextList.ItemsSource = now.Next;
                NextEmpty.Visibility = now.Next.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            MoveCursor();
        }

        // ------------------------------------------------------------------------------------------------
        // CHART: one bar per sample of the AirspaceTimeline (every sliding step) from the first opening to the last closing
        // of the sector within the simulation. Bar = entries in the window shown at that time, coloured by status; grey =
        // closed; dark line = aircraft inside the sector; dashed line = capacity per window; blue line = timeline time.
        // Click or drag moves the timeline; the wheel zooms around the mouse, Shift + wheel moves, double-click shows all
        // ------------------------------------------------------------------------------------------------

        private static readonly Brush AircraftLine = Charts.Frozen(0x1F, 0x2D, 0x3D, 0xB0); // dark, a bit transparent: the bars show through
        private static readonly TimeSpan MIN_VIEW = TimeSpan.FromHours(1);

        private DateTime _fullFrom, _fullTo;        // the sector's whole range
        private DateTime? _viewFrom, _viewTo;       // zoomed range, null = the whole one
        private bool _dragging;

        private double ChartWidth => Math.Max(0, Chart.ActualWidth - LEFT - RIGHT);
        private double ChartHeight => Math.Max(0, Chart.ActualHeight - TOP - BOTTOM);

        private double X(DateTime t) => LEFT + (t - _from).TotalSeconds / Math.Max(1, (_to - _from).TotalSeconds) * ChartWidth;

        private DateTime TimeAt(double x) =>
            _from + TimeSpan.FromSeconds(Math.Max(0, Math.Min(1, (x - LEFT) / Math.Max(1, ChartWidth))) * (_to - _from).TotalSeconds);

        private void DrawChart()
        {
            Chart.Children.Clear();
            _cursor = null;
            if (_info == null || ChartWidth <= 0)
                return;
            AirspaceTimeline tl = _info.Timeline;
            if (tl == null || _info.Row < 0)
            {
                Charts.Label(Chart, tl == null ? "Computing..." : "No data for this sector", LEFT, TOP + 20, 11);
                return;
            }

            // Whole range: its open periods within the simulation (the whole simulation if none); shown: the zoom, if any
            DateTime first = tl.Starts[0], last = tl.Starts[tl.Starts.Count - 1] + tl.Step;
            _fullFrom = _info.OpenPeriods.Count > 0 ? Max(first, _info.OpenPeriods[0].Start) : first;
            _fullTo = _info.OpenPeriods.Count > 0 ? Min(last, _info.OpenPeriods[_info.OpenPeriods.Count - 1].End) : last;
            if (_fullTo <= _fullFrom)
            {
                _fullFrom = first;
                _fullTo = last;
            }
            _from = _viewFrom ?? _fullFrom;
            _to = _viewTo ?? _fullTo;

            // Heights from the whole range, so zooming does not change them
            int row = _info.Row, maxValue = 0;
            for (int k = 0; k < tl.Starts.Count; k++)
                if (tl.Starts[k] >= _fullFrom && tl.Starts[k] < _fullTo)
                    maxValue = Math.Max(maxValue, Math.Max(tl.Demand[row, k], _info.AircraftInside?[k] ?? 0));
            double top = Math.Max(1, Math.Max(maxValue, _info.CapacityPerWindow ?? 0)) * 1.15;
            double h = ChartHeight, y0 = TOP + h;
            double Y(double v) => y0 - v / top * h;

            var aircraft = new Polyline { Stroke = AircraftLine, StrokeThickness = 1.1, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            for (int k = 0; k < tl.Starts.Count; k++)
            {
                DateTime s = tl.Starts[k];
                if (s + tl.Step <= _from || s >= _to)
                    continue;
                double x0 = X(Max(s, _from)), x1 = X(Min(s + tl.Step, _to));
                sbyte status = tl.Status[row, k];
                if (status == AirspaceTimeline.CLOSED)
                    Charts.Rect(Chart, x0, TOP, x1 - x0, h, Charts.Closed);
                else if (tl.Demand[row, k] > 0)
                    Charts.Rect(Chart, x0, Y(tl.Demand[row, k]), Math.Max(1, x1 - x0 - 0.3), y0 - Y(tl.Demand[row, k]), Charts.StatusBrush(status));
                if (_info.AircraftInside != null)
                    aircraft.Points.Add(new Point((x0 + x1) / 2, Y(_info.AircraftInside[k])));
            }
            Chart.Children.Add(aircraft);

            // Axes, capacity line, labels
            Charts.Line(Chart, LEFT, y0, LEFT + ChartWidth, y0, Charts.Axis);
            Charts.Line(Chart, LEFT, TOP, LEFT, y0, Charts.Axis);
            Charts.Label(Chart, "0", LEFT - 3, y0 - 7, 9, alignRight: true);
            Charts.Label(Chart, Math.Ceiling(top).ToString(CultureInfo.InvariantCulture), LEFT - 3, TOP - 2, 9, alignRight: true);
            if (_info.CapacityPerWindow != null)
            {
                double yc = Y(_info.CapacityPerWindow.Value);
                Charts.Line(Chart, LEFT, yc, LEFT + ChartWidth, yc, Brushes.Black, 1, new DoubleCollection { 4, 3 });
                Charts.Label(Chart, "cap " + _info.CapacityPerWindow.Value.ToString("0.#", CultureInfo.InvariantCulture), LEFT + ChartWidth, yc - 13, 9, alignRight: true);
            }
            double hours = (_to - _from).TotalHours;
            Charts.TimeAxis(Chart, _from, _to, LEFT, ChartWidth, y0, hours > 24 ? 12 : hours > 10 ? 4 : hours > 4 ? 2 : hours > 2 ? 1 : 0.5);
            MoveCursor();
        }

        private void MoveCursor()
        {
            if (_info?.Timeline == null || ChartWidth <= 0 || _to <= _from)
                return;
            bool inside = _time >= _from && _time <= _to;
            if (_cursor == null)
            {
                _cursor = Charts.Line(Chart, 0, TOP, 0, TOP + ChartHeight, Charts.Cursor, 2);
                _cursor.IsHitTestVisible = false;
            }
            _cursor.Visibility = inside ? Visibility.Visible : Visibility.Collapsed;
            double x = X(_time);
            _cursor.X1 = _cursor.X2 = x;
        }

        private void Chart_SizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();

        // Sample under the mouse, -1 outside the chart
        private int SampleAt(Point p)
        {
            AirspaceTimeline tl = _info?.Timeline;
            if (tl == null || _info.Row < 0 || p.X < LEFT || p.X > LEFT + ChartWidth || _to <= _from)
                return -1;
            return tl.IndexAt(TimeAt(p.X));
        }

        private void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(Chart);
            if (_dragging && e.LeftButton != MouseButtonState.Pressed)
                _dragging = false;  // released outside (capture lost): stop following the mouse
            if (_dragging)
            {
                TimeRequested?.Invoke(TimeAt(p.X));
                return;
            }
            int k = SampleAt(p);
            if (k < 0)
            {
                ChartReadout.Text = " ";
                return;
            }
            AirspaceTimeline tl = _info.Timeline;
            var (ws, we) = tl.Windows[k];
            string when = $"{tl.Starts[k]:dd/MM HH:mm}";
            string inside = _info.AircraftInside == null ? "" : $", {_info.AircraftInside[k]} aircraft inside";
            if (tl.Status[_info.Row, k] == AirspaceTimeline.CLOSED)
                ChartReadout.Text = $"{when}: closed";
            else
            {
                int d = tl.Demand[_info.Row, k];
                string load = _info.CapacityPerWindow == null ? "no limit" : (d / _info.CapacityPerWindow.Value * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
                ChartReadout.Text = $"{when}: window {ws:HH:mm}-{we:HH:mm}, {d} entries, {load}{inside}";
            }
        }

        private void Chart_MouseLeave(object sender, MouseEventArgs e) => ChartReadout.Text = " ";

        // Press: the timeline goes there and follows the mouse until the button is released. Double-click: show all
        private void Chart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                _viewFrom = _viewTo = null;
                DrawChart();
                return;
            }
            Point p = e.GetPosition(Chart);
            if (SampleAt(p) < 0)
                return;
            _dragging = true;
            Chart.CaptureMouse();
            TimeRequested?.Invoke(TimeAt(p.X));
        }

        private void Chart_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            Chart.ReleaseMouseCapture();
        }

        // Capture taken away (Alt+Tab, a pop-up, the window losing focus): the drag ends
        private void Chart_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

        private void Chart_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_info?.Timeline == null || _fullTo <= _fullFrom)
                return;
            e.Handled = true; // the panel does not scroll while zooming
            DateTime from = _from, to = _to;
            TimeSpan span = to - from;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                TimeSpan shift = TimeSpan.FromTicks(span.Ticks / 10 * (e.Delta < 0 ? 1 : -1));
                from += shift;
                to += shift;
            }
            else
            {
                DateTime at = TimeAt(e.GetPosition(Chart).X);
                TimeSpan newSpan = TimeSpan.FromTicks((long)(span.Ticks * (e.Delta > 0 ? 0.75 : 1 / 0.75)));
                if (newSpan < MIN_VIEW) newSpan = MIN_VIEW;
                if (newSpan >= _fullTo - _fullFrom) { _viewFrom = _viewTo = null; DrawChart(); return; }
                double f = (at - from).TotalSeconds / span.TotalSeconds;
                from = at - TimeSpan.FromSeconds(f * newSpan.TotalSeconds);
                to = from + newSpan;
            }
            if (from < _fullFrom) { to += _fullFrom - from; from = _fullFrom; }
            if (to > _fullTo) { from -= to - _fullTo; to = _fullTo; }
            _viewFrom = from;
            _viewTo = to;
            DrawChart();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

        private void Export_Click(object sender, RoutedEventArgs e) => ExportRequested?.Invoke(this, EventArgs.Empty);

        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    }
}
