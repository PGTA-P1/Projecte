using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Projecte1_WPF_.ArcGisMethods;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;
using Projecte1_WPF_.Simulation;
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
        // Checks > Crossings of sample flights: each call sign covers a case. AFL2605 LEMD departure climbing through the TMA
        // bands, AAF151 overflight at cruise, LEGCY13 V-22 that never leaves Morón, ICC03 has a vertical-only segment,
        // RAM990 only clips one sector near the Strait, TAP1082 LPPT->LECO flies under the ACCs (no intervals -> dropped)
        private static readonly string[] CHECK_FLIGHTS = { "AFL2605", "AAF151", "LEGCY13", "ICC03", "RAM990", "TAP1082" };

        // Timeline: entry/exit markers stay this long in real time, whatever the speed
        private static readonly TimeSpan MARKER_LIFE = TimeSpan.FromSeconds(2);

        // Simulated seconds per real second
        private static readonly int[] SPEEDS = { 1, 10, 60, 120, 300, 600, 1800 };
        private const int DEFAULT_SPEED = 60;

        private readonly mainProgram _program;
        private readonly PlotOpenSectors _openSectors;   // configurations open at the timeline time
        private readonly PlotAircraft _aircraft;         // timeline aircraft and entry/exit markers
        private readonly PlotAirspaces3D _checkSectors;  // sectors crossed by the check flights
        private readonly PlotTrajectories3D _checkFlights;

        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Stopwatch _clock = new Stopwatch();
        private TimeSimulation _simulation;  // null until the entries are computed
        private DateTime _simTime;
        private double _lastReal;            // _clock seconds at the previous tick
        private double _speed = DEFAULT_SPEED;
        private bool _playing;
        private bool _sliderByCode;          // slider moved by SetTime, not by the user

        public MainWindow(mainProgram program)
        {
            InitializeComponent();
            _program = program;
            MySceneView.Scene = new Scene(BasemapStyle.ArcGISImagery);
            _openSectors = new PlotOpenSectors(MySceneView, program);
            _checkSectors = new PlotAirspaces3D(MySceneView, Color.FromArgb(120, 190, 0, 190)); // magenta
            _checkFlights = new PlotTrajectories3D(MySceneView);
            _aircraft = new PlotAircraft(MySceneView);

            SpeedBox.ItemStringFormat = "{0}x";
            foreach (int speed in SPEEDS)
                SpeedBox.Items.Add(speed);
            SpeedBox.SelectedItem = DEFAULT_SPEED;
            _timer.Tick += Timer_Tick;
        }

        // ------------------------------------------------------------------------------------------------
        // LOAD DATA: the window starts empty. Default data = the files of the Archives folder, filtered by accs.txt.
        // Loading, the sector outlines and the entries run in the background; then the airspace open when the first plane
        // that crosses the ACCs takes off is drawn and Start simulation is enabled
        // ------------------------------------------------------------------------------------------------
        private async void LoadDefaultData_Click(object sender, RoutedEventArgs e)
        {
            DefaultDataItem.IsEnabled = false; // loaded once per run
            try
            {
                StatusText.Text = "Loading the default data...";
                await Task.Run(() => _program.LoadFiltered());
                FrameArea();

                StatusText.Text = $"Loaded {_program.Trajectories.Count:N0} trajectories and {_program.Configurations.Count} configurations of " +
                                  $"{_program.Accs.Count} ACCs. Computing sectors and entries (about 10 s)...";
                Task<string> entries = Task.Run(() => _program.ComputeEntries());
                Task<PlotOpenSectors.SectorOutlines> outlines = Task.Run(() => PlotOpenSectors.BuildOutlines(_program));
                _openSectors.SetOutlines(await outlines);
                Debug.WriteLine("[Load] " + await entries);

                _simulation = new TimeSimulation(_program.RelevantTrajectories, _program.Entries);
                TimeSlider.Minimum = 0;
                TimeSlider.Maximum = (_simulation.End - _simulation.Start).TotalSeconds;
                TimeSlider.SmallChange = 60;
                TimeSlider.LargeChange = 3600;

                // Airspace at the start of the simulation; the aircraft appear when it starts
                _openSectors.Update(_simulation.Start);
                ChecksMenu.IsEnabled = true;
                StartSimBTN.IsEnabled = true;
                int openEntries = _program.Entries.Count(x => x.Level == EntryLevel.Open);
                StatusText.Text = $"{_simulation.FlightCount:N0} flights cross the ACCs, {openEntries:N0} sector entries. " +
                                  $"Airspace open at {_simulation.Start:dd/MM HH:mm} UTC, when the first of them takes off.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                StatusText.Text = "Could not load the data: " + ex.Message;
                DefaultDataItem.IsEnabled = true;
            }
        }

        // Shows the timeline and plays from the start (again from the start if pressed later)
        private void StartSimulation_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation == null)
                return;
            CloseCheck();
            TimelineBar.Visibility = Visibility.Visible;
            int openEntries = _program.Entries.Count(x => x.Level == EntryLevel.Open);
            StatusText.Text = $"{_simulation.FlightCount:N0} flights cross the ACCs, {openEntries:N0} sector entries.";
            SetTime(_simulation.Start, continuous: false);
            if (!_timer.IsEnabled)
            {
                _clock.Start();
                _timer.Start();
            }
            SetPlaying(true);
        }

        // Straight-down view of the whole area of the ACCs
        private void FrameArea()
        {
            LatLonBox a = _program.Area;
            if (a != null)
                _ = MySceneView.SetViewpointAsync(new Viewpoint(new Envelope(a.MinLon, a.MinLat, a.MaxLon, a.MaxLat, SpatialReferences.Wgs84)));
        }

        // ------------------------------------------------------------------------------------------------
        // TIMELINE: relevant trajectories as yellow dots (only while inside the airspace of the ACCs), the configurations
        // open at each moment, and entries (green) / exits (red) of open sectors flashing for MARKER_LIFE
        // ------------------------------------------------------------------------------------------------

        private void SetPlaying(bool playing)
        {
            _playing = playing;
            PlayBTN.Content = playing ? "Pause" : "Play";
            _lastReal = _clock.Elapsed.TotalSeconds;
        }

        private void PlayBTN_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation == null)
                return;
            CloseCheck();
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
                if (next >= _simulation.End)
                {
                    next = _simulation.End;
                    SetPlaying(false);
                }
                SetTime(next, continuous: true);
            }
            _aircraft.RemoveExpiredMarkers();
        }

        // continuous = playing forward: markers for the entries/exits passed since the last update. A jump clears them
        private void SetTime(DateTime time, bool continuous)
        {
            if (continuous && time > _simTime)
                _aircraft.AddMarkers(_simulation.EventsBetween(_simTime, time), MARKER_LIFE);
            else if (!continuous)
                _aircraft.ClearMarkers();
            _simTime = time;

            _openSectors.Update(time);
            List<AircraftPosition> positions = _simulation.PositionsAt(time);
            _aircraft.UpdateAircraft(positions);

            _sliderByCode = true;
            TimeSlider.Value = (time - _simulation.Start).TotalSeconds;
            _sliderByCode = false;
            TimeText.Text = time.ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
            AirText.Text = $"{positions.Count} aircraft, {_openSectors.OpenSectorCount} open sectors";
        }

        // ------------------------------------------------------------------------------------------------
        // CHECKS (menu): results in the side panel. The crossing check also draws its flights and hides the timeline layers
        // until the panel is closed
        // ------------------------------------------------------------------------------------------------

        private void ShowCheck(string title, string text)
        {
            CheckTitle.Text = title;
            CheckText.Text = text;
            CheckPanel.Visibility = Visibility.Visible;
        }

        private void CloseCheck()
        {
            CheckPanel.Visibility = Visibility.Collapsed;
            _checkSectors.Clear();
            _checkFlights.Clear();
            _openSectors.IsVisible = true;
            _aircraft.IsVisible = true;
        }

        private void CloseCheck_Click(object sender, RoutedEventArgs e)
        {
            CloseCheck();
        }

        // Filtered load against the full load of every file (loads everything twice: a few seconds)
        private async void CheckFilter_Click(object sender, RoutedEventArgs e)
        {
            CloseCheck();
            ShowCheck("ACC filter", "Running: every file is loaded twice (filtered and full)...");
            try { CheckText.Text = await Task.Run(() => new mainProgram().TestFilter()); }
            catch (Exception ex) { CheckText.Text = ex.ToString(); }
        }

        // Crossing of CHECK_FLIGHTS only, compared with independent oracles; draws the flights (yellow line), the elementary
        // sectors they cross (magenta), entries (big green) and exits (small red on top)
        private async void CheckCrossings_Click(object sender, RoutedEventArgs e)
        {
            CloseCheck();
            SetPlaying(false);
            ShowCheck("Crossings of sample flights", "Running...");
            try
            {
                List<CrossingCheck.FlightResult> results = null;
                string report = await Task.Run(() => CrossingCheck.Run(_program, CHECK_FLIGHTS, out results));
                int summary = report.LastIndexOf("SUMMARY", StringComparison.Ordinal);
                CheckText.Text = report.Substring(summary) + Environment.NewLine + report.Substring(0, summary);

                _openSectors.IsVisible = false;
                _aircraft.IsVisible = false;

                var crossed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = new List<(double, double, double)>();
                var exits = new List<(double, double, double)>();
                foreach (CrossingCheck.FlightResult r in results)
                {
                    if (r.Flight == null)
                        continue;
                    _checkFlights.PlotFlight(r.Flight, Color.Yellow);
                    if (r.Inside == null)
                        continue;
                    crossed.UnionWith(r.Inside.Keys);
                    foreach (List<Interval> list in r.Inside.Values)
                        foreach (Interval i in list)
                        {
                            entries.Add(CrossingCheck.PositionAt(r.Flight, i.Start));
                            exits.Add(CrossingCheck.PositionAt(r.Flight, i.End));
                        }
                }
                _checkFlights.PlotPoints(entries, Color.LimeGreen, 15);
                _checkFlights.PlotPoints(exits, Color.Red, 8);

                var outlines = new List<MergedAirblocks>();
                foreach (ElementarySector es in _program.Elementary)
                    if (crossed.Contains(es.Name))
                        outlines.AddRange(AirblockMerger.Merge(es, _program.Airblocks));
                await _checkSectors.PlotAsync(outlines, zoomTo: false);

                // Straight-down view of the crossed sectors (GlobeHelper.FlyTo's tilted view misses country-sized extents)
                if (outlines.Count > 0)
                {
                    var box = new EnvelopeBuilder(SpatialReferences.Wgs84);
                    bool first = true;
                    foreach (MergedAirblocks o in outlines)
                        foreach (var ring in o.OuterRings)
                            foreach (var v in ring)
                            {
                                if (first) { box.SetXY(v.Longitude, v.Latitude, v.Longitude, v.Latitude); first = false; }
                                else box.UnionOf(new MapPoint(v.Longitude, v.Latitude, SpatialReferences.Wgs84));
                            }
                    await MySceneView.SetViewpointAsync(new Viewpoint(box.ToGeometry()));
                }
            }
            catch (Exception ex)
            {
                CheckText.Text = ex.ToString();
            }
        }
    }
}
