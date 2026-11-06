using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.ArcGisMethods;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;
using Projecte1_WPF_.Simulation;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Projecte1_WPF_
{
    public partial class MainWindow : Window
    {
        // Timeline: entry/exit markers stay this long in real time, whatever the speed
        private static readonly TimeSpan MARKER_LIFE = TimeSpan.FromSeconds(2);

        // Simulated seconds per real second
        private static readonly int[] SPEEDS = { 1, 10, 60, 120, 300, 600, 1800 };
        private const int DEFAULT_SPEED = 60;

        private readonly mainProgram _program;
        private readonly PlotOpenSectors _openSectors;   // configurations open at the timeline time
        private readonly PlotAircraft _aircraft;         // timeline aircraft and entry/exit markers

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Stopwatch _clock = new Stopwatch();
        private TimeSimulation _simulation;  // null until the entries are computed
        private SectorDemand _demand;        // demand of the open sectors against capacity (sector colours), with _simulation
        private DateTime _simTime;
        private double _lastReal;            // _clock seconds at the previous tick
        private double _speed = DEFAULT_SPEED;
        private bool _playing;
        private bool _sliderByCode;          // slider moved by SetTime, not by the user

        // Results > Export entries writes Exports/entries.csv next to the program
        private static string ExportsFolder { get { return System.IO.Path.Combine(AppContext.BaseDirectory, "Exports"); } }
        private const string ENTRIES_CSV = "entries.csv";

        public MainWindow(mainProgram program)
        {
            InitializeComponent();
            _program = program;
            MySceneView.Scene = new Scene(BasemapStyle.ArcGISImagery);
            MyMapView.Map = new Map(BasemapStyle.ArcGISImagery);
            _openSectors = new PlotOpenSectors(MySceneView, MyMapView, program);
            _aircraft = new PlotAircraft(MySceneView);
            LineOptions_Changed(null, null); // 2D line options as the check boxes start

            // Rotation of the 2D map with the right mouse button; the compass follows both views
            MyMapView.PreviewMouseRightButtonDown += MapView_RightDown;
            MyMapView.PreviewMouseMove += MapView_MouseMove;
            MyMapView.PreviewMouseRightButtonUp += MapView_RightUp;
            MyMapView.ViewpointChanged += (s, a) => { if (_twoD) UpdateCompass(); };
            MySceneView.ViewpointChanged += (s, a) => { if (!_twoD) UpdateCompass(); };

            SpeedBox.ItemStringFormat = "{0}x";
            foreach (int speed in SPEEDS)
                SpeedBox.Items.Add(speed);
            SpeedBox.SelectedItem = DEFAULT_SPEED;
            _timer.Tick += Timer_Tick;
            FillParameterBoxes(); // the values the load will use
            InitSectorList();
            MyMapView.GeoViewDoubleTapped += Map_DoubleTapped;
            MySceneView.GeoViewDoubleTapped += Map_DoubleTapped;
            PreviewKeyDown += Window_PreviewKeyDown;
        }

        // ------------------------------------------------------------------------------------------------
        // STATUS LINE and progress bar for the work that runs in the background (loading, export)
        // ------------------------------------------------------------------------------------------------

        // fraction 0..1, or null when the length is unknown (moving bar)
        private void ShowProgress(string message, double? fraction)
        {
            StatusText.Text = message;
            WorkProgress.Visibility = Visibility.Visible;
            WorkProgress.IsIndeterminate = fraction == null;
            if (fraction != null)
                WorkProgress.Value = fraction.Value * WorkProgress.Maximum;
        }

        private void HideProgress(string message)
        {
            StatusText.Text = message;
            WorkProgress.Visibility = Visibility.Collapsed;
        }

        // ------------------------------------------------------------------------------------------------
        // LOAD DATA: the window starts empty. Default data = the files of the Archives folder, filtered by accs.txt.
        // Loading, the sector outlines and the entries run in the background with a progress bar; then the airspace open
        // when the first plane that crosses the ACCs takes off is drawn and Start simulation is enabled
        // ------------------------------------------------------------------------------------------------
        private async void LoadDefaultData_Click(object sender, RoutedEventArgs e)
        {
            DefaultDataItem.IsEnabled = false; // loaded once per run
            ApplyParamBTN.IsEnabled = false;   // ComputeEntries reads the parameters while it runs
            try
            {
                ShowProgress("Loading the files of the Archives folder...", null);
                await Task.Run(() => _program.LoadFiltered());
                FrameArea();

                // Entries of every trajectory (parallel, a few seconds) and the outlines of every sector at the same time
                int flights = _program.Trajectories.Count;
                string loaded = $"Loaded {flights:N0} trajectories and {_program.Configurations.Count} configurations of {_program.Accs.Count} ACCs. ";
                var progress = new Progress<double>(f =>
                    ShowProgress(loaded + $"Computing the sector entries: {f * flights:N0} of {flights:N0} trajectories...", f));
                ShowProgress(loaded + "Computing the sector entries...", 0);
                Task<string> entries = Task.Run(() => _program.ComputeEntries(progress));
                Task<PlotOpenSectors.SectorOutlines> outlines = Task.Run(() => PlotOpenSectors.BuildOutlines(_program));
                // Waits for both, also when one fails, so no progress message arrives after this. Never await inside
                // Debug.WriteLine: a Release build removes the call with its arguments, and the await with it
                await Task.WhenAll(entries, outlines);
                _openSectors.SetOutlines(outlines.Result);
                Debug.WriteLine("[Load] " + entries.Result);
                await ApplyFlightLevelsAsync(); // a range chosen before loading (the outlines were built for all levels)

                await BuildSimulationAsync();
                StartSimBTN.IsEnabled = true;
                HideProgress($"{_simulation.FlightCount:N0} flights cross the ACCs, {OpenEntryCount():N0} sector entries ({EntryParametersText()}). " +
                             $"Airspace open at {_simulation.Start:dd/MM HH:mm} UTC, when the first of them takes off. Ready: Start simulation.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                HideProgress("Could not load the data: " + ex.Message);
                DefaultDataItem.IsEnabled = true;
            }
            finally
            {
                ApplyParamBTN.IsEnabled = true;
            }
        }

        // Simulation and demand of the current entries; the airspace (coloured) at the start, or at the timeline time once
        // the timeline is shown
        private async Task BuildSimulationAsync()
        {
            ShowProgress("Preparing the simulation...", null);
            _simulation = await Task.Run(() => new TimeSimulation(_program.RelevantTrajectories, _program.Entries));
            RebuildDemand();
            TimeSlider.Minimum = 0;
            TimeSlider.Maximum = (_simulation.End - _simulation.Start).TotalSeconds;
            TimeSlider.SmallChange = 60;
            TimeSlider.LargeChange = 3600;

            // Airspace at the start of the simulation; the aircraft appear when it starts
            if (TimelineBar.Visibility == Visibility.Visible)
                SetTime(_simulation.Start, continuous: false);
            else
            {
                _openSectors.Update(_simulation.Start);
                RecolorSectors(_simulation.Start);
            }
        }

        // Demand of the current entries with the current window and step (fast: no recomputation of the entries)
        private void RebuildDemand()
        {
            _demand = new SectorDemand(_program.Entries, _program.Capacities, _program.DemandParameters, _simulation.Start);
            _demandVersion++; // the sector list shows the new values
        }

        // Colour of every open sector: its entries in the window shown at that time against its capacity in the window;
        // the sector list follows
        private void RecolorSectors(DateTime time)
        {
            var (start, end) = _demand.WindowAt(time);
            _openSectors.Recolor(name => _demand.Status(_demand.Demand(name, start, end), _demand.Capacity(name)));
            RefreshSectorList(time);
        }

        private int OpenEntryCount()
        {
            return _program.Entries.Count(x => x.Level == EntryLevel.Open);
        }

        private string EntryParametersText()
        {
            return $"minimum time {_program.Parameters.MinTimeInSector:0.##} s, reentry {_program.Parameters.ReentryTime:0.##} s";
        }

        // Shows the timeline and plays from the start (again from the start if pressed later)
        private void StartSimulation_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation == null)
                return;
            TimelineBar.Visibility = Visibility.Visible;
            ResultsBar.Visibility = Visibility.Visible; // results of the simulation, available once it has been run
            StatusText.Text = $"{_simulation.FlightCount:N0} flights cross the ACCs, {OpenEntryCount():N0} sector entries ({EntryParametersText()}).";
            SetTime(_simulation.Start, continuous: false);
            if (!_timer.IsEnabled)
            {
                _clock.Start();
                _timer.Start();
            }
            SetPlaying(true);
        }

        // ------------------------------------------------------------------------------------------------
        // RESULTS > Export entries: only when clicked, a quick pre-simulation in the background replays the simulation from
        // start to end, collects every entry/exit it plots into a table and writes it to Exports/entries.csv, with the
        // parameters used on its first lines and a progress bar. The live timeline is not touched; nothing big is shown on
        // screen. Any error is reported in the status line, never thrown
        // ------------------------------------------------------------------------------------------------
        private async void ExportEntries_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation == null)
                return;
            string csv = System.IO.Path.Combine(ExportsFolder, ENTRIES_CSV);
            ExportEntriesItem.IsEnabled = false;
            var progress = new Progress<double>(f =>
                ShowProgress($"Exporting the entries: replaying the simulation from start to end ({f * 100:F0} %)...", f));
            ShowProgress("Exporting the entries: replaying the simulation from start to end...", 0);
            try
            {
                TimeSimulation simulation = _simulation;
                var parameters = ExportParameters(simulation);
                DataTable table = await Task.Run(() => EntryTable.FromSimulation(simulation, progress));
                ShowProgress($"Exporting the entries: writing {table.Rows.Count:N0} rows to {ENTRIES_CSV}...", null);
                await Task.Run(() => EntryTable.SaveCsv(table, csv, parameters));
                HideProgress($"Saved {table.Rows.Count:N0} entries ({simulation.Start:dd/MM HH:mm} - {simulation.End:dd/MM HH:mm} UTC, " +
                             $"{EntryParametersText()}) to {csv}");
            }
            catch (Exception ex)
            {
                HideProgress($"Could not save the entries (is {ENTRIES_CSV} open in Excel?): {ex.Message}");
            }
            finally
            {
                ExportEntriesItem.IsEnabled = true;
            }
        }

        // First lines of the exported file: the parameters that produced these entries (the entry ones are always those of
        // the last computation: the panel only changes them by recomputing) and the demand ones in use
        private List<(string, string)> ExportParameters(TimeSimulation simulation)
        {
            var inv = CultureInfo.InvariantCulture;
            return new List<(string, string)>
            {
                ("Entries of the simulation", $"{simulation.Start.ToString("dd/MM/yyyy HH:mm", inv)} - {simulation.End.ToString("dd/MM/yyyy HH:mm", inv)} UTC"),
                ("Minimum time in sector (s)", _program.Parameters.MinTimeInSector.ToString(inv)),
                ("Reentry time (s)", _program.Parameters.ReentryTime.ToString(inv)),
                ("Integration window (min)", _program.DemandParameters.WindowMinutes.ToString(inv) + ", half-open [start, end)"),
                ("Sliding step (min)", _program.DemandParameters.StepMinutes.ToString(inv)),
                ("Traffic file", "20160728Initial_initial.so6"),
                ("Exported", DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss", inv) + " UTC"),
            };
        }

        private void OpenExports_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(ExportsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ExportsFolder + "\"") { UseShellExecute = true });
        }

        // Straight-down, north-up view of the whole area of the ACCs, in both views (animated). Also the home button of the
        // map. The 2D map is turned north up first: framing the area on a rotated map kept (part of) the rotation
        private async void FrameArea()
        {
            LatLonBox a = _program.Area;
            if (a == null)
                return;
            var area = new Viewpoint(new Envelope(a.MinLon, a.MinLat, a.MaxLon, a.MaxLat, SpatialReferences.Wgs84));
            MapButtons.Visibility = Visibility.Visible;
            _ = MySceneView.SetViewpointAsync(area);
            try
            {
                if (MyMapView.MapRotation != 0)
                    await MyMapView.SetViewpointRotationAsync(0);
                await MyMapView.SetViewpointAsync(area);
            }
            catch (Exception ex) { Debug.WriteLine("[FrameArea] " + ex.Message); } // display only (e.g. map not ready yet)
        }

        private void HomeView_Click(object sender, RoutedEventArgs e)
        {
            FrameArea();
        }

        // ------------------------------------------------------------------------------------------------
        // ROTATION. The 3D scene rotates with the mouse (right-drag) by itself; the 2D map only rotates with a two-finger
        // touch twist, so here: right-drag on the map and the 15-degree buttons. The compass needle follows the heading
        // of the view shown, and clicking it puts north up again
        // ------------------------------------------------------------------------------------------------

        private const double ROTATE_STEP_DEG = 15;
        private const double DRAG_DEG_PER_PX = 0.4;  // right-drag: degrees per pixel moved sideways
        private System.Windows.Point? _rotateFrom;    // right-drag start (null: not dragging)
        private double _rotateFromAngle;

        private void Compass_Click(object sender, RoutedEventArgs e)
        {
            if (_twoD)
                _ = MyMapView.SetViewpointRotationAsync(0);
            else if (MySceneView.Camera is Camera c)
                _ = MySceneView.SetViewpointCameraAsync(c.RotateTo(0, c.Pitch, c.Roll));
        }

        private void RotateLeft_Click(object sender, RoutedEventArgs e) => _ = MyMapView.SetViewpointRotationAsync(MyMapView.MapRotation + ROTATE_STEP_DEG);

        private void RotateRight_Click(object sender, RoutedEventArgs e) => _ = MyMapView.SetViewpointRotationAsync(MyMapView.MapRotation - ROTATE_STEP_DEG);

        private void MapView_RightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _rotateFrom = e.GetPosition(MyMapView);
            _rotateFromAngle = MyMapView.MapRotation;
            MyMapView.CaptureMouse();
            e.Handled = true;
        }

        private void MapView_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_rotateFrom == null)
                return;
            double dx = e.GetPosition(MyMapView).X - _rotateFrom.Value.X;
            Viewpoint vp = MyMapView.GetCurrentViewpoint(ViewpointType.CenterAndScale);
            if (vp?.TargetGeometry is MapPoint center)
                MyMapView.SetViewpoint(new Viewpoint(center, vp.TargetScale, _rotateFromAngle - dx * DRAG_DEG_PER_PX));
        }

        private void MapView_RightUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (_rotateFrom == null)
                return;
            _rotateFrom = null;
            MyMapView.ReleaseMouseCapture();
            e.Handled = true;
        }

        // Needle angle (clockwise on screen) = where north is: minus the map rotation in 2D (MapRotation grows when the map
        // turns counter-clockwise, "rotate left"), minus the camera heading in 3D
        private void UpdateCompass()
        {
            CompassNeedle.Angle = _twoD ? -MyMapView.MapRotation : -(MySceneView.Camera?.Heading ?? 0);
        }

        // ------------------------------------------------------------------------------------------------
        // VIEW: 2D map or 3D globe (switch at the top of the left strip). Both are always up to date (PlotOpenSectors draws
        // the prisms and the plan lines in the scene, the plan fills and lines in the map); switching shows the other control
        // at the same area and moves the labels and the aircraft to it
        // ------------------------------------------------------------------------------------------------

        private bool _twoD;

        private void View2D_Checked(object sender, RoutedEventArgs e) => SetView(twoD: true);

        private void View3D_Checked(object sender, RoutedEventArgs e) => SetView(twoD: false);

        private void SetView(bool twoD)
        {
            if (_openSectors == null || twoD == _twoD) // the XAML checks 3D while the window is being built
                return;
            GeoView from = twoD ? MySceneView : MyMapView;
            GeoView to = twoD ? MyMapView : MySceneView;
            Viewpoint area = from.GetCurrentViewpoint(ViewpointType.BoundingGeometry);
            if (area != null)
                to.SetViewpoint(area);
            MyMapView.Visibility = twoD ? Visibility.Visible : Visibility.Collapsed;
            MySceneView.Visibility = twoD ? Visibility.Collapsed : Visibility.Visible;
            _openSectors.ShowIn2D(twoD);
            _aircraft.MoveTo(to);
            LevelStepsBox.Visibility = twoD ? Visibility.Visible : Visibility.Collapsed; // 3D shows the heights itself
            RotateButtons.Visibility = twoD ? Visibility.Visible : Visibility.Collapsed;   // 3D rotates with the mouse itself
            _twoD = twoD;
            UpdateCompass();
        }

        // ------------------------------------------------------------------------------------------------
        // FLIGHT LEVELS SHOWN (two-handle slider of the strip, FL 0-600, 600 = no upper limit). Display only: the sectors
        // are cut to the range (3D prisms cut, 2D plans rebuilt for the bands in it, sectors entirely outside not drawn)
        // and the aircraft and markers outside it are hidden; entries, demand and colours do not change. Applied
        // FL_APPLY_DELAY after the last move, so dragging does not rebuild the plans at every step
        // ------------------------------------------------------------------------------------------------

        private const int FL_SLIDER_MAX = 600;       // top of the slider = no upper limit (FL999 in the files)
        private const int FL_MIN_GAP = 10;           // the two handles stay at least this far apart
        private static readonly TimeSpan FL_APPLY_DELAY = TimeSpan.FromMilliseconds(350);
        private DispatcherTimer _flApplyTimer;
        private int _flVersion;                      // newest range asked for; an older plan build is dropped

        private (int From, int To) FlRangeChosen()
        {
            int from = (int)FlFromSlider.Value, to = (int)FlToSlider.Value;
            return (from, to >= FL_SLIDER_MAX ? SectorPlanBuilder.NO_UPPER_LIMIT : to);
        }

        private void FlSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FlToSlider == null || FlFromSlider == null) // both are built before the handlers matter
                return;
            // Keep the handles apart: the one moved pushes the other
            if (FlToSlider.Value - FlFromSlider.Value < FL_MIN_GAP)
            {
                if (sender == FlFromSlider) FlToSlider.Value = Math.Min(FL_SLIDER_MAX, FlFromSlider.Value + FL_MIN_GAP);
                if (FlToSlider.Value - FlFromSlider.Value < FL_MIN_GAP) FlFromSlider.Value = FlToSlider.Value - FL_MIN_GAP;
            }
            int from = (int)FlFromSlider.Value, to = (int)FlToSlider.Value;
            FlRangeText.Text = $"FL{from:000} - {(to >= FL_SLIDER_MAX ? "600+" : to.ToString("000"))}";
            UpdateFlRangeFill();

            if (_flApplyTimer == null)
            {
                _flApplyTimer = new DispatcherTimer { Interval = FL_APPLY_DELAY };
                _flApplyTimer.Tick += async (s, a) => { _flApplyTimer.Stop(); await ApplyFlightLevelsAsync(); };
            }
            _flApplyTimer.Stop();
            _flApplyTimer.Start();
        }

        private void FlRangeTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateFlRangeFill();

        // Blue part of the track between the two handles (the handle centres move between 7 px and width - 7 px)
        private void UpdateFlRangeFill()
        {
            double usable = Math.Max(0, FlRangeTrack.ActualWidth - 14);
            double left = FlFromSlider.Value / FL_SLIDER_MAX * usable, right = FlToSlider.Value / FL_SLIDER_MAX * usable;
            FlRangeFill.Margin = new Thickness(7 + left, 0, 0, 0);
            FlRangeFill.Width = Math.Max(0, right - left);
        }

        // Rebuilds the 2D plans for the chosen range in the background and redraws; nothing to do before the data is
        // loaded (the load applies the range) or when the range did not change
        private async Task ApplyFlightLevelsAsync()
        {
            var (from, to) = FlRangeChosen();
            if (_openSectors.Outlines == null || (from == _openSectors.FlFrom && to == _openSectors.FlTo))
                return;
            int version = ++_flVersion;
            var outlines = _openSectors.Outlines;
            Dictionary<string, SectorPlan> plans = await Task.Run(() => PlotOpenSectors.BuildPlans(outlines, from, to));
            if (version != _flVersion)
                return; // the handles moved again meanwhile: that build is applied instead
            _openSectors.SetFlightLevels(from, to, plans);
            if (_simulation == null)
                return;
            if (TimelineBar.Visibility == Visibility.Visible)
                SetTime(_simTime, continuous: false);
            else
            {
                _openSectors.Update(_simulation.Start);
                RecolorSectors(_simulation.Start);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // SECTOR LIST (left strip): the open sectors drawn at the timeline time (so the flight-level filter applies) with
        // ACC, configuration, demand in the window, nominal capacity per hour and load. Rebuilt when the drawn sectors
        // change (DrawVersion) or the demand is rebuilt; otherwise only its values are updated, so the selection and the
        // sort stay. A search box filters it. Selecting rows shows only those sectors and their aircraft on the map;
        // double-click (row or sector on the map) also zooms to it; Show all / Esc shows everything again
        // ------------------------------------------------------------------------------------------------

        public class SectorRow : System.ComponentModel.INotifyPropertyChanged
        {
            private static readonly System.Windows.Media.Brush GreenBrush = Frozen(0xB9, 0xE4, 0xB9);
            private static readonly System.Windows.Media.Brush YellowBrush = Frozen(0xF6, 0xE0, 0x8A);
            private static readonly System.Windows.Media.Brush RedBrush = Frozen(0xF2, 0xA0, 0xA0);

            public string Sector { get; set; }
            public string Acc { get; set; }
            public string Configuration { get; set; }
            public double? Hourly { get; set; }       // nominal capacity per hour, null = no limit
            public string CapacityText => Hourly == null ? "none" : Hourly.Value.ToString("0.#", CultureInfo.InvariantCulture);
            public double CapacitySort => Hourly ?? double.MaxValue;

            public int Demand { get; private set; }
            public double Load { get; private set; }  // demand / capacity in the window; -1 = no limit
            public string LoadText => Load < 0 ? "-" : (Load * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
            public System.Windows.Media.Brush StatusBrush { get; private set; }

            public void SetValues(int demand, double? capacityInWindow, CapacityStatus status)
            {
                Demand = demand;
                Load = capacityInWindow == null ? -1 : demand / capacityInWindow.Value;
                StatusBrush = status == CapacityStatus.Red ? RedBrush : status == CapacityStatus.Yellow ? YellowBrush : GreenBrush;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(null)); // all values
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            private static System.Windows.Media.Brush Frozen(byte r, byte g, byte b)
            {
                var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }
        }

        private readonly System.Collections.ObjectModel.ObservableCollection<SectorRow> _sectorRows =
            new System.Collections.ObjectModel.ObservableCollection<SectorRow>();
        private System.ComponentModel.ICollectionView _sectorView;
        private int _demandVersion;
        private (DateTime WindowStart, int DrawVersion, int DemandVersion) _listKey;
        private int _listDrawVersion = -1, _listDemandVersion = -1;
        private bool _gridByCode;             // selection changed by RefreshSectorList, not by the user

        private void InitSectorList()
        {
            SectorGrid.ItemsSource = _sectorRows;
            _sectorView = System.Windows.Data.CollectionViewSource.GetDefaultView(_sectorRows);
            _sectorView.Filter = o =>
            {
                string q = SectorSearchBox.Text.Trim();
                if (q.Length == 0) return true;
                var r = (SectorRow)o;
                return Contains(r.Sector, q) || Contains(r.Acc, q) || Contains(r.Configuration, q);
            };
        }

        private static bool Contains(string text, string part) =>
            text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private void RefreshSectorList(DateTime time)
        {
            if (_demand == null)
                return;
            var (start, end) = _demand.WindowAt(time);
            var key = (start, _openSectors.DrawVersion, _demandVersion);
            if (key == _listKey)
                return;
            _listKey = key;

            if (_openSectors.DrawVersion != _listDrawVersion || _demandVersion != _listDemandVersion)
            {
                // Other sectors drawn (or a new demand): new rows, the selected names selected again
                var selected = new HashSet<string>(SectorGrid.SelectedItems.Cast<SectorRow>().Select(r => r.Sector), StringComparer.OrdinalIgnoreCase);
                _gridByCode = true;
                _sectorRows.Clear();
                foreach (var (acc, config, name) in _openSectors.DrawnSectors().OrderBy(x => x.Sector, StringComparer.OrdinalIgnoreCase))
                    _sectorRows.Add(new SectorRow { Sector = name, Acc = acc, Configuration = config, Hourly = _demand.HourlyCapacity(name) });
                foreach (SectorRow row in _sectorRows.Where(r => selected.Contains(r.Sector)))
                    SectorGrid.SelectedItems.Add(row);
                _gridByCode = false;
                _listDrawVersion = _openSectors.DrawVersion;
                _listDemandVersion = _demandVersion;
            }
            foreach (SectorRow row in _sectorRows)
            {
                int demand = _demand.Demand(row.Sector, start, end);
                double? capacity = _demand.Capacity(row.Sector);
                row.SetValues(demand, capacity, _demand.Status(demand, capacity));
            }
        }

        private void SectorSearch_TextChanged(object sender, TextChangedEventArgs e) => _sectorView?.Refresh();

        // Rows selected by the user: only those sectors (and their aircraft) on the map; none selected = all
        private void SectorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_gridByCode)
                return;
            List<string> names = SectorGrid.SelectedItems.Cast<SectorRow>().Select(r => r.Sector).ToList();
            ShowOnlySectors(names.Count == 0 ? null : names);
        }

        private void SectorGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SectorGrid.SelectedItem is SectorRow row)
                SelectAndZoom(row.Sector);
        }

        private void ShowAll_Click(object sender, RoutedEventArgs e) => ShowAllSectors();

        private void ShowAllSectors()
        {
            _gridByCode = true;
            SectorGrid.UnselectAll();
            _gridByCode = false;
            ShowOnlySectors(null);
        }

        // names = null: everything again
        private void ShowOnlySectors(ICollection<string> names)
        {
            _openSectors.ShowOnly(names);
            ShowAllBTN.Visibility = names == null ? Visibility.Collapsed : Visibility.Visible;
            if (_simulation != null && TimelineBar.Visibility == Visibility.Visible)
                SetTime(_simTime, continuous: false); // aircraft of the selected sectors only
        }

        // One sector: selected in the list (scrolled into view), the only one on the map, and zoomed to
        private void SelectAndZoom(string name)
        {
            _gridByCode = true;
            SectorGrid.UnselectAll();
            SectorRow row = _sectorRows.FirstOrDefault(r => string.Equals(r.Sector, name, StringComparison.OrdinalIgnoreCase));
            if (row != null)
            {
                SectorGrid.SelectedItems.Add(row);
                // After the redraw and layout, otherwise the row is selected but not scrolled to
                Dispatcher.BeginInvoke(new Action(() => SectorGrid.ScrollIntoView(row)), System.Windows.Threading.DispatcherPriority.Background);
            }
            _gridByCode = false;
            ShowOnlySectors(new[] { name });

            var box = _openSectors.ExtentOf(name);
            if (box == null)
                return;
            var (minLat, minLon, maxLat, maxLon) = box.Value;
            double marginLat = (maxLat - minLat) * 0.3 + 0.05, marginLon = (maxLon - minLon) * 0.3 + 0.05;
            var area = new Viewpoint(new Envelope(minLon - marginLon, minLat - marginLat, maxLon + marginLon, maxLat + marginLat, SpatialReferences.Wgs84));
            if (_twoD)
                _ = MyMapView.SetViewpointAsync(area);
            else
                _ = MySceneView.SetViewpointAsync(area);
        }

        // Double-click on a sector of the map: the same as double-clicking its row (instead of the default zoom in).
        // The first drawn sector under the click (fills, prisms, outlines or labels carry its name)
        private async void Map_DoubleTapped(object sender, Esri.ArcGISRuntime.UI.Controls.GeoViewInputEventArgs e)
        {
            if (_simulation == null)
                return;
            e.Handled = true;
            try
            {
                var view = (GeoView)sender;
                var results = await view.IdentifyGraphicsOverlaysAsync(e.Position, 4, false, 10);
                string name = results.SelectMany(r => r.Graphics)
                                     .Select(g => g.Attributes.TryGetValue("name", out object n) ? n as string : null)
                                     .FirstOrDefault(n => n != null && _openSectors.IsDrawn(n));
                if (name != null)
                    SelectAndZoom(name);
            }
            catch (Exception ex) { Debug.WriteLine("[Map_DoubleTapped] " + ex.Message); }
        }

        // Esc: close the parameters pop-up, otherwise show all the sectors again
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Escape)
                return;
            if (ParamsPopup.IsOpen)
                ParamsPopup.IsOpen = false;
            else if (_openSectors.Only != null)
                ShowAllSectors();
        }

        // Plan lines: dashed borders inside collapsed sectors (both views), dotted height steps (2D map only)
        private void LineOptions_Changed(object sender, RoutedEventArgs e)
        {
            if (_openSectors == null)
                return;
            _openSectors.ShowMemberBorders = MemberBordersBox.IsChecked == true;
            _openSectors.ShowLevelSteps = LevelStepsBox.IsChecked == true;
        }

        // ------------------------------------------------------------------------------------------------
        // TIMELINE: relevant trajectories as yellow dots (only while inside the airspace of the ACCs), the configurations
        // open at each moment, and entries (green) / exits (red) of open sectors flashing for MARKER_LIFE
        // ------------------------------------------------------------------------------------------------

        private void SetPlaying(bool playing)
        {
            _playing = playing;
            bool atEnd = _simulation != null && _simTime >= _simulation.End;
            PlayBTN.Content = playing ? "Pause" : atEnd ? "Replay" : "Play";
            _lastReal = _clock.Elapsed.TotalSeconds;
        }

        private void PlayBTN_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation == null)
                return;
            if (!_playing && _simTime >= _simulation.End)
                SetTime(_simulation.Start, continuous: false); // restart from the beginning
            SetPlaying(!_playing);
        }

        private void SpeedBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SpeedBox.SelectedItem is int speed)
                _speed = speed;
        }

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderByCode || _simulation == null)
                return;
            SetTime(_simulation.Start.AddSeconds(e.NewValue), continuous: false);
        }

        // Runs always (also paused) so markers disappear after MARKER_LIFE of real time; moves the time only when playing
        private void Timer_Tick(object sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double realSeconds = now - _lastReal;
            _lastReal = now;

            if (_playing)
            {
                DateTime next = _simTime.AddSeconds(realSeconds * _speed);
                bool finished = next >= _simulation.End;
                if (finished)
                    next = _simulation.End;
                SetTime(next, continuous: true);
                if (finished)
                {
                    // Tell the user why it stopped and where the results are
                    SetPlaying(false);
                    int entries = _program.Entries.Count(x => x.Level == EntryLevel.Open);
                    StatusText.Text = $"Simulation finished at {_simulation.End:dd/MM HH:mm} UTC: {_simulation.FlightCount:N0} flights, " +
                                      $"{entries:N0} sector entries. Results > Export entries saves them.";
                }
            }
            _aircraft.RemoveExpiredMarkers();
        }

        // continuous = playing forward: markers for the entries/exits passed since the last update. A jump clears them
        private void SetTime(DateTime time, bool continuous)
        {
            // Aircraft and markers only between the flight levels shown (display filter)
            int flFrom = _openSectors.FlFrom, flTo = _openSectors.FlTo;
            bool Shown(double fl) => fl >= flFrom && (fl < flTo || flTo >= SectorPlanBuilder.NO_UPPER_LIMIT);
            // With sectors selected (list or double-click), only the aircraft inside them and their markers
            IReadOnlyCollection<string> only = _openSectors.Only;
            bool Selected(IEnumerable<string> sectors) => only == null || sectors.Any(s => only.Contains(s));
            if (continuous && time > _simTime)
                _aircraft.AddMarkers(_simulation.EventsBetween(_simTime, time).Where(ev => Shown(ev.FL) && Selected(new[] { ev.Sector })).ToList(), MARKER_LIFE);
            else if (!continuous)
                _aircraft.ClearMarkers();
            _simTime = time;

            _openSectors.Update(time);
            RecolorSectors(time);
            List<AircraftPosition> positions = _simulation.PositionsAt(time).Where(a => Shown(a.FL) && Selected(a.Sectors)).ToList();
            _aircraft.UpdateAircraft(positions);

            _sliderByCode = true;
            TimeSlider.Value = (time - _simulation.Start).TotalSeconds;
            _sliderByCode = false;
            TimeText.Text = time.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            AirText.Text = AirDescription(time, positions.Count);
        }

        // What the map shows now, always the same length (monospaced, every field padded) so the time slider keeps its
        // width: aircraft shown, open sectors shown, near the limit, overloaded, demand window. With no aircraft the first
        // field says why the map is empty (finished, or when the next one appears), so it does not look stopped
        private string AirDescription(DateTime time, int aircraft)
        {
            var (start, end) = _demand.WindowAt(time);
            string planes;
            if (time >= _simulation.End)
                planes = "finished";
            else if (aircraft > 0)
                planes = $"{aircraft} aircraft";
            else
            {
                DateTime? next = _simulation.NextEntryAfter(time);
                planes = next == null ? "no aircraft" : $"next {next.Value:HH:mm}";
            }
            return $"{planes,-12} {_openSectors.OpenSectorCount,2} open {_openSectors.NearLimitCount,2} near " +
                   $"{_openSectors.OverloadedCount,2} over  {start:HH:mm}-{end:HH:mm}";
        }

        // ------------------------------------------------------------------------------------------------
        // PARAMETERS strip (always visible on the right, collapsible): the entry parameters (minimum time, reentry time)
        // and the demand ones (integration window, sliding step). Before loading they are only stored and used by the
        // load. After loading, window/step apply at once (only the counting changes); a new minimum/reentry time needs the
        // entries computed again, which the user confirms (Recompute entries)
        // ------------------------------------------------------------------------------------------------

        private (double MinTime, double Reentry) _pendingEntryParameters; // waiting for Recompute entries

        // Boxes = the values in use
        private void FillParameterBoxes()
        {
            var inv = CultureInfo.InvariantCulture;
            MinTimeBox.Text = _program.Parameters.MinTimeInSector.ToString(inv);
            ReentryBox.Text = _program.Parameters.ReentryTime.ToString(inv);
            WindowBox.Text = _program.DemandParameters.WindowMinutes.ToString(inv);
            StepBox.Text = _program.DemandParameters.StepMinutes.ToString(inv);
        }

        // « hides the strip and leaves a narrow "Parameters »" button that shows it again
        private void ToggleParameters_Click(object sender, RoutedEventArgs e)
        {
            bool show = ParamPanel.Visibility != Visibility.Visible;
            ParamPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            ParamShowBTN.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            if (show)
                SectorSearchBox.Focus();
        }

        // Toolbar Parameters: the pop-up with the current values (or the values waiting for Recompute entries)
        private void ParamsMenu_Click(object sender, RoutedEventArgs e)
        {
            if (RecomputeRow.Visibility != Visibility.Visible)
            {
                FillParameterBoxes();
                ParamMessage.Text = "";
            }
            ParamsPopup.IsOpen = true;
            MinTimeBox.Focus();
        }

        // Enter applies, Esc closes (a pop-up has no default button of its own)
        private void ParamsPopup_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyParameters_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Escape)
            {
                ParamsPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        private void ApplyParameters_Click(object sender, RoutedEventArgs e)
        {
            RecomputeRow.Visibility = Visibility.Collapsed;
            if (!ReadParameter(MinTimeBox, "Minimum time in sector", 0, out double minTime) ||
                !ReadParameter(ReentryBox, "Reentry time", 0, out double reentry) ||
                !ReadParameter(WindowBox, "Integration window", 1, out double window) ||
                !ReadParameter(StepBox, "Sliding step", 1, out double step))
                return;
            if (step > window)
            {
                ParamMessage.Text = "The sliding step cannot be longer than the integration window (some moments would be in no window).";
                return;
            }

            // Demand: only the counting changes, applied now
            var messages = new List<string>();
            if (window != _program.DemandParameters.WindowMinutes || step != _program.DemandParameters.StepMinutes)
            {
                _program.DemandParameters.WindowMinutes = window;
                _program.DemandParameters.StepMinutes = step;
                if (_simulation != null)
                {
                    RebuildDemand();
                    RecolorSectors(_simTime > _simulation.Start ? _simTime : _simulation.Start);
                    if (TimelineBar.Visibility == Visibility.Visible)
                        AirText.Text = AirDescription(_simTime, _simulation.PositionsAt(_simTime).Count);
                }
                messages.Add($"Window {window:0.##} min and step {step:0.##} min applied.");
            }

            // Entries: stored as they are before loading; after loading the entries must be computed again
            if (minTime != _program.Parameters.MinTimeInSector || reentry != _program.Parameters.ReentryTime)
            {
                if (_simulation == null)
                {
                    _program.Parameters.MinTimeInSector = minTime;
                    _program.Parameters.ReentryTime = reentry;
                    messages.Add("Minimum and reentry time saved: they will be used when the data is loaded.");
                }
                else
                {
                    _pendingEntryParameters = (minTime, reentry);
                    messages.Add($"Minimum time {minTime:0.##} s and reentry time {reentry:0.##} s need the entries computed " +
                                 "again (a few seconds). The timeline goes back to the start. Recompute now?");
                    RecomputeRow.Visibility = Visibility.Visible;
                }
            }
            ParamMessage.Text = messages.Count > 0 ? string.Join(" ", messages) : "Nothing changed.";
        }

        // Number >= min in the box, or a message in the panel
        private bool ReadParameter(TextBox box, string name, double min, out double value)
        {
            string text = box.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min)
                return true;
            ParamMessage.Text = $"{name}: write a number of at least {min}.";
            box.Focus();
            return false;
        }

        private void CancelRecompute_Click(object sender, RoutedEventArgs e)
        {
            RecomputeRow.Visibility = Visibility.Collapsed;
            MinTimeBox.Text = _program.Parameters.MinTimeInSector.ToString(CultureInfo.InvariantCulture);
            ReentryBox.Text = _program.Parameters.ReentryTime.ToString(CultureInfo.InvariantCulture);
            ParamMessage.Text = "Entries kept as they were (" + EntryParametersText() + ").";
        }

        // Entries computed again with the confirmed minimum/reentry time, then a new simulation and demand from the start
        private async void RecomputeEntries_Click(object sender, RoutedEventArgs e)
        {
            RecomputeRow.Visibility = Visibility.Collapsed;
            SetPlaying(false);
            ApplyParamBTN.IsEnabled = StartSimBTN.IsEnabled = ExportEntriesItem.IsEnabled = false;
            ParamMessage.Text = "Computing the entries again...";
            try
            {
                _program.Parameters.MinTimeInSector = _pendingEntryParameters.MinTime;
                _program.Parameters.ReentryTime = _pendingEntryParameters.Reentry;
                int flights = _program.Trajectories.Count;
                var progress = new Progress<double>(f =>
                    ShowProgress($"Computing the sector entries ({EntryParametersText()}): {f * flights:N0} of {flights:N0} trajectories...", f));
                ShowProgress($"Computing the sector entries ({EntryParametersText()})...", 0);
                string summary = await Task.Run(() => _program.ComputeEntries(progress));
                Debug.WriteLine("[Recompute] " + summary);
                await BuildSimulationAsync();
                string done = $"{_simulation.FlightCount:N0} flights cross the ACCs, {OpenEntryCount():N0} sector entries ({EntryParametersText()}).";
                HideProgress(done + (TimelineBar.Visibility == Visibility.Visible ? " Timeline back at the start: Play to continue." : ""));
                ParamMessage.Text = "Entries recomputed: " + done;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                HideProgress("Could not compute the entries: " + ex.Message);
                ParamMessage.Text = "Could not compute the entries: " + ex.Message;
            }
            finally
            {
                ApplyParamBTN.IsEnabled = StartSimBTN.IsEnabled = ExportEntriesItem.IsEnabled = true;
            }
        }
    }
}
