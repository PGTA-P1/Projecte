using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.ArcGisMethods;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;
using Projecte1_WPF_.ReadData;
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

        // A marker also lasts at most this much simulated time. Up to 300x that is longer than MARKER_LIFE, so nothing
        // changes there; at 1800x two real seconds were a simulated hour, thousands of markers on the map at once, each one
        // added and removed by hand: the main cost of a tick at that speed
        private static readonly TimeSpan MARKER_MAX_SIMULATED = TimeSpan.FromMinutes(10);

        private TimeSpan MarkerLife() =>
            TimeSpan.FromSeconds(Math.Min(MARKER_LIFE.TotalSeconds, MARKER_MAX_SIMULATED.TotalSeconds / Math.Max(1, _speed)));

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
        // EXPORT FOLDER: where every export is saved. Chosen by the user (location and name) in ExportFolderWindow, asked
        // once before the first export and changed in Results > Export folder…; kept between sessions (ExportFolder)
        private string _exportsFolder = ExportFolder.Load();
        private string ExportsFolder { get { return _exportsFolder ?? ExportFolder.Default; } }

        // Before an export: the first time (no folder chosen yet, in this or an earlier session) the user is asked.
        // False = the user cancelled, so nothing is exported
        private bool HaveExportFolder()
        {
            return _exportsFolder != null || ChooseExportFolder();
        }

        private bool ChooseExportFolder()
        {
            var window = new ExportFolderWindow(ExportsFolder) { Owner = this };
            if (window.ShowDialog() != true)
                return false;
            _exportsFolder = window.Folder;
            ExportFolder.Save(_exportsFolder);
            ShowNotice("Exports are saved in " + _exportsFolder);
            return true;
        }

        private void ExportFolder_Click(object sender, RoutedEventArgs e) => ChooseExportFolder();

        private const string ENTRIES_CSV = "entries.csv";

        public MainWindow(mainProgram program)
        {
            InitializeComponent();
            _program = program;
            // Copies of the user's files live in a session folder: deleted on close; those of crashed sessions now
            UserDataFolder.DeleteStale();
            Closed += (s, e) => UserDataFolder.DeleteSession();
            MySceneView.Scene = new Scene(BasemapStyle.ArcGISImagery);
            MyMapView.Map = new Map(BasemapStyle.ArcGISImagery);
            _openSectors = new PlotOpenSectors(MySceneView, MyMapView, program);
            _aircraft = new PlotAircraft(MySceneView);
            _density = new PlotDensity(MySceneView, MyMapView);
            LineOptions_Changed(null, null); // 2D line options as the check boxes start
            // Density legend: the same continuous ramp as the image
            var ramp = new System.Windows.Media.LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
            for (int i = 0; i < PlotDensity.RAMP.Length; i++)
            {
                System.Drawing.Color c = PlotDensity.RAMP[i];
                ramp.GradientStops.Add(new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(c.R, c.G, c.B), i / (PlotDensity.RAMP.Length - 1.0)));
            }
            DensityColors.Children.Add(new System.Windows.Shapes.Rectangle { Height = 10, RadiusX = 2, RadiusY = 2, Fill = ramp });

            // Rotation of the 2D map with the right mouse button; the compass follows both views
            MyMapView.PreviewMouseRightButtonDown += MapView_RightDown;
            MyMapView.PreviewMouseMove += MapView_MouseMove;
            MyMapView.PreviewMouseRightButtonUp += MapView_RightUp;
            MyMapView.ViewpointChanged += (s, a) => { if (_twoD) UpdateCompass(); };
            MySceneView.ViewpointChanged += (s, a) => { if (!_twoD) UpdateCompass(); };

            foreach (int speed in SPEEDS)
            {
                var item = new MenuItem { Header = speed + "x", Tag = speed, IsCheckable = true, IsChecked = speed == DEFAULT_SPEED };
                item.Click += SpeedItem_Click;
                SpeedMenu.Items.Add(item);
            }
            SpeedText.Text = DEFAULT_SPEED + "x";
            _timer.Tick += Timer_Tick;
            SectorInfoPanel.CloseRequested += SectorPanel_CloseRequested;
            SectorInfoPanel.TimeRequested += GoToTime;
            SectorInfoPanel.ExportRequested += SectorPanel_ExportRequested;
            ComparePanelView.OpenRequested += SelectAndZoom;
            ComparePanelView.RemoveRequested += name => SetTicked(name, false);
            ComparePanelView.CloseRequested += (s, e) => ShowAllSectors();  // closing a panel flies home (PanelClosed)
            ComparePanelView.ExportRequested += ComparePanel_ExportRequested;

            // Last resort: an unexpected error on the UI thread is reported instead of closing the app without a word
            System.Windows.Application.Current.DispatcherUnhandledException += (s, args) =>
            {
                Debug.WriteLine("[Unhandled] " + args.Exception);
                HideProgress("Unexpected error: " + args.Exception.Message);
                MessageBox.Show(this, args.Exception.Message + "\n\nThe app keeps running; if something looks wrong, load the data again.",
                                "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
                args.Handled = true;
            };
            FillParameterBoxes(); // the values the load will use
            InitSectorList();
            MyMapView.GeoViewDoubleTapped += Map_DoubleTapped;
            MySceneView.GeoViewDoubleTapped += Map_DoubleTapped;
            PreviewKeyDown += Window_PreviewKeyDown;
        }

        // ------------------------------------------------------------------------------------------------
        // WORK IN PROGRESS for what runs in the background (loading, computing, exporting): a box over the bottom of the
        // map with the message and a bar, shown only meanwhile. The outcome (totals, file saved, error) is a fading notice.
        // There is no status line any more: the user found it hard to see and not worth its row
        // ------------------------------------------------------------------------------------------------

        // fraction 0..1, or null when the length is unknown (moving bar)
        private void ShowProgress(string message, double? fraction)
        {
            StatusText.Text = message;
            WorkBox.Visibility = Visibility.Visible;
            EmptyHint.Visibility = Visibility.Collapsed;
            WorkProgress.IsIndeterminate = fraction == null;
            if (fraction != null)
                WorkProgress.Value = fraction.Value * WorkProgress.Maximum;
        }

        private void HideProgress(string message)
        {
            StatusText.Text = message;
            WorkBox.Visibility = Visibility.Collapsed;
            EmptyHint.Visibility = _simulation == null ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrEmpty(message))
                ShowNotice(message);
        }

        // ------------------------------------------------------------------------------------------------
        // LOAD DATA: the window starts empty. Default data = the files of the Archives folder, filtered by accs.txt.
        // Loading, the sector outlines and the entries run in the background with a progress bar; then the airspace open
        // when the first plane that crosses the ACCs takes off is drawn and Start simulation is enabled
        // ------------------------------------------------------------------------------------------------
        private async void LoadDefaultData_Click(object sender, RoutedEventArgs e)
        {
            await LoadDataAsync(DataFiles.Default());
        }

        // Your own files: the data files window (the files chosen last time shown again), then the same load
        private DataFiles _lastOwnFiles;

        private async void OwnFiles_Click(object sender, RoutedEventArgs e)
        {
            var window = new DataFilesWindow(_lastOwnFiles) { Owner = this };
            if (window.ShowDialog() != true)
                return;
            _lastOwnFiles = window.Files;
            await LoadDataAsync(window.Files);
        }

        // Loads a data set (the default files or the user's). A data set already loaded is replaced, after a confirmation:
        // simulation, panels, analysis and map are cleared first. A file that cannot be read, or files that do not fit
        // together, stop the load with the reader's message (file, line, what is wrong)
        private async Task LoadDataAsync(DataFiles files)
        {
            // Never two computations on the same data: the load replaces the lists the entry computation is reading
            if (_recomputing || !DefaultDataItem.IsEnabled)
            {
                ShowNotice("Wait until the current computation ends, then load the data");
                return;
            }
            if (_simulation != null &&
                MessageBox.Show(this, "Load another data set? The current simulation, its entries and analysis are cleared.",
                                "Load data", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
            ClearLoadedData();
            DefaultDataItem.IsEnabled = OwnFilesItem.IsEnabled = false;
            ApplyParamBTN.IsEnabled = false;   // ComputeEntries reads the parameters while it runs
            bool loadDefaultInstead = false;   // the user's answer when the files do not match
            try
            {
                ShowProgress(files.IsDefault ? "Loading the default data..." : "Loading your files...", null);
                await Task.Run(() => _program.LoadFiltered(files));

                // The files read well but do not fully match each other (e.g. the opening scheme opens configurations the
                // .cfg does not have): the user decides. Yes = go on without what does not match, No = the default data
                // instead, Cancel = nothing loaded
                if (_program.Mismatches.Count > 0)
                {
                    string list = string.Join("\n", _program.Mismatches.Take(6).Select(m => "• " + m)) +
                                  (_program.Mismatches.Count > 6 ? $"\n… and {_program.Mismatches.Count - 6} more" : "");
                    MessageBoxResult answer = MessageBox.Show(this,
                        "The files do not match each other:\n\n" + list +
                        "\n\nLoad anyway, without these?\nNo = load the default data.   Cancel = load nothing.",
                        "The files do not match", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                    if (answer != MessageBoxResult.Yes)
                    {
                        ClearLoadedData();
                        loadDefaultInstead = answer == MessageBoxResult.No;
                        HideProgress(loadDefaultInstead ? "" : "Nothing was loaded: the files do not match each other.");
                        throw new OperationCanceledException();   // out of the load; the default data, if chosen, after the finally
                    }
                }
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
                SetFlTop();
                await ApplyFlightLevelsAsync(force: true); // the range chosen (the outlines were built for all levels)

                // Files that read well but do not meet: no flight inside an open sector (e.g. other dates)
                if (OpenEntryCount() == 0)
                    throw new DataFileException(files[DataRole.OpeningScheme],
                        $"no flight enters an open sector: does the opening scheme ({_program.OpeningScheme.FirstStart:dd/MM/yyyy} - " +
                        $"{_program.OpeningScheme.LastEnd:dd/MM/yyyy}) cover the days of the traffic " +
                        $"({_program.Trajectories.Min(t => t.Start):dd/MM/yyyy} - {_program.Trajectories.Max(t => t.End):dd/MM/yyyy})?");

                await BuildSimulationAsync();
                AnalysisMenu.IsEnabled = true;
                MapFilterBTN.Visibility = Visibility.Visible;
                ShowTimeline();
                string warnings = _program.LoadWarnings.Count == 0 ? "" : " Note: " + string.Join("; ", _program.LoadWarnings) + ".";
                HideProgress($"{_simulation.FlightCount:N0} flights cross the ACCs, {OpenEntryCount():N0} sector entries ({EntryParametersText()}). " +
                             $"Airspace open at {_simulation.Start:dd/MM HH:mm} UTC, when the first of them takes off. Press Play to run it.{warnings}");
            }
            catch (OperationCanceledException)
            {
                // The user's answer to "the files do not match": already cleared and said
            }
            catch (DataFileException ex)
            {
                // A genuine error: the user must fix or replace a file, so it is shown in full, with the names of the files
                // the user chose (the readers see the session copies, e.g. Accs.txt)
                ClearLoadedData();
                string message = ex.Message;
                foreach (DataRoleInfo r in DataFiles.Roles)
                    if (files[r.Role] != null)
                        message = message.Replace(System.IO.Path.GetFileName(files[r.Role]), files.SourceName(r.Role));
                HideProgress("Could not load the data: " + message.Replace("\n", " "));
                MessageBox.Show(this, message + "\n\nFix the file, or choose another one in Load Data > Your own files.",
                                "Could not load the data", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex) // anything else (e.g. NTS geometry of an odd user file): reported, never a crash
            {
                Debug.WriteLine(ex);
                // A failure inside the parallel computation arrives wrapped ("One or more errors occurred"): show the cause
                string cause = (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
                ClearLoadedData();
                HideProgress("Could not load the data: " + cause);
                MessageBox.Show(this, cause + "\n\nNothing was loaded.", "Could not load the data", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ApplyParamBTN.IsEnabled = true;
                DefaultDataItem.IsEnabled = OwnFilesItem.IsEnabled = true;
            }
            if (loadDefaultInstead)
                await LoadDataAsync(DataFiles.Default());
        }

        // Everything that belongs to the data set loaded: before loading another one, or after a failed load
        private void ClearLoadedData()
        {
            if (_simulation == null && _program.Files == null)
                return;
            SetPlaying(false);
            // Also the ticks of a comparison: they (and the map's "only these sectors") used to survive the load, so
            // the new data set came up with no sector on the map when it did not have those sectors
            if (_focusSector != null || _ticked.Count > 0 || _openSectors.Only != null)
            {
                _clearingData = true;   // no flight home: the load frames the new area
                ShowAllSectors();
                _clearingData = false;
            }
            _pausedBeforeClosing = null;
            _analysis?.Close();
            if (ShowDensityBox.IsChecked == true)
                SectorsLayerBTN.IsChecked = true;
            _aircraft.Clear();
            _openSectors.Clear();
            _simulation = null;
            _demand = null;
            _timelineTask = null;
            _flightsById.Clear();
            _openByFlightSource = null;
            _sectorData.Clear();
            _selectedSector = null;
            _sectorRows.Clear();
            _listKey = default;
            SectorSearchBox.Text = "";
            OpenPeriodMarks.Children.Clear();
            TimelineBar.Visibility = ResultsBar.Visibility = MapFilterBTN.Visibility = Visibility.Collapsed;
            AnalysisMenu.IsEnabled = false;
        }

        // Simulation and demand of the current entries; the airspace (coloured) at the start, or at the timeline time once
        // the timeline is shown
        private async Task BuildSimulationAsync()
        {
            ShowProgress("Preparing the simulation...", null);
            if (_focusSector != null)
                ShowAllSectors();   // its panel was built from the previous entries
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
            StartTimeline();  // analysis views and sector panel chart
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

        // Once the data is loaded: the timeline at the start of the simulation, paused (Play runs it), with Analysis and
        // Results. There is no Start simulation button any more: the user found it unnecessary
        private void ShowTimeline()
        {
            if (_simulation == null)
                return;
            TimelineBar.Visibility = Visibility.Visible;
            ResultsBar.Visibility = Visibility.Visible;
            SetTime(_simulation.Start, continuous: false);
            if (!_timer.IsEnabled)
            {
                _clock.Start();
                _timer.Start();   // runs while paused too, so markers expire
            }
            SetPlaying(false);
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
            if (!HaveExportFolder())
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
                ("Capacity per window", "hourly capacity x window / 60; overload = demand above it"),
                ("Data", _program.Files == null || _program.Files.IsDefault ? "default data" : "your own files"),
                ("Traffic file", _program.Files?.SourceName(DataRole.Trajectories) ?? ""),
                ("Exported", DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss", inv) + " UTC"),
            };
        }

        // ------------------------------------------------------------------------------------------------
        // RESULTS > the analysed data (AirspaceTimeline) to CSV, in the background: demand per sector and time, traffic
        // over time, sector summary, overloads; or all four at once. Same parameter lines as entries.csv
        // ------------------------------------------------------------------------------------------------

        private async void ExportDemand_Click(object sender, RoutedEventArgs e) => await ExportAnalysisAsync(AnalysisExport.DEMAND_CSV);
        private async void ExportTraffic_Click(object sender, RoutedEventArgs e) => await ExportAnalysisAsync(AnalysisExport.TRAFFIC_CSV);
        private async void ExportSectors_Click(object sender, RoutedEventArgs e) => await ExportAnalysisAsync(AnalysisExport.SECTORS_CSV);
        private async void ExportOverloads_Click(object sender, RoutedEventArgs e) => await ExportAnalysisAsync(AnalysisExport.OVERLOADS_CSV);
        private async void ExportAll_Click(object sender, RoutedEventArgs e) =>
            await ExportAnalysisAsync(AnalysisExport.DEMAND_CSV, AnalysisExport.TRAFFIC_CSV, AnalysisExport.SECTORS_CSV, AnalysisExport.OVERLOADS_CSV);

        private static int WriteAnalysis(string file, AirspaceTimeline tl, string path, List<(string, string)> parameters)
        {
            switch (file)
            {
                case AnalysisExport.DEMAND_CSV: return AnalysisExport.Demand(tl, path, parameters);
                case AnalysisExport.TRAFFIC_CSV: return AnalysisExport.Traffic(tl, path, parameters);
                case AnalysisExport.SECTORS_CSV: return AnalysisExport.Sectors(tl, path, parameters);
                default: return AnalysisExport.Overloads(tl, path, parameters);
            }
        }

        private async Task ExportAnalysisAsync(params string[] files)
        {
            if (_simulation == null)
                return;
            if (!HaveExportFolder())
                return;
            ShowProgress($"Exporting {string.Join(", ", files)}...", null);
            try
            {
                AirspaceTimeline tl = await TimelineAsync();
                if (tl == null)
                    throw new InvalidOperationException("the analysis could not be computed");
                var parameters = ExportParameters(_simulation);
                var written = await Task.Run(() => files.Select(f => $"{f} ({WriteAnalysis(f, tl, System.IO.Path.Combine(ExportsFolder, f), parameters):N0} rows)").ToList());
                HideProgress($"Saved {string.Join(", ", written)} in {ExportsFolder}");
            }
            catch (Exception ex) // called from async void handlers: never let it close the app
            {
                HideProgress(ex is System.IO.IOException ? $"Could not export (is a file open in Excel?): {ex.Message}" : $"Could not export: {ex.Message}");
            }
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
        // FLIGHT LEVELS SHOWN (two-handle slider of the strip, FL 0 to the traffic top, see SetFlTop). Display only: the sectors
        // are cut to the range (3D prisms cut, 2D plans rebuilt for the bands in it, sectors entirely outside not drawn)
        // and the aircraft and markers outside it are hidden; entries, demand and colours do not change. Applied
        // FL_APPLY_DELAY after the last move, so dragging does not rebuild the plans at every step
        // ------------------------------------------------------------------------------------------------

        // Top of the slider: the highest flight level the traffic reaches + 10, rounded up to 10 (SetFlTop after a load;
        // 600 before). At the top the range has no upper limit: sectors are drawn whole and every aircraft is shown
        private int FL_SLIDER_MAX => (int)FlToSlider.Maximum;
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
            FlRangeText.Text = $"FL{from:000} - {to:000}";
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

        // The slider ends at the highest flight level of the traffic that crosses the ACCs + 10 (rounded up to 10), not at
        // a fixed 600: the top of the scale is then the real top of the traffic. A handle at the old top moves to the new one
        private void SetFlTop()
        {
            int max = 0;
            foreach (var f in _program.RelevantTrajectories)
                foreach (var s in f.Segments)
                    max = Math.Max(max, Math.Max(s.FLBegin, s.FLEnd));
            int top = Math.Max(FL_MIN_GAP, (int)Math.Ceiling((max + 10) / 10.0) * 10);
            bool toAtTop = FlToSlider.Value >= FlToSlider.Maximum;
            FlFromSlider.Maximum = FlToSlider.Maximum = top;
            if (toAtTop || FlToSlider.Value > top)
                FlToSlider.Value = top;
            if (FlFromSlider.Value > top - FL_MIN_GAP)
                FlFromSlider.Value = top - FL_MIN_GAP;
            FlSlider_ValueChanged(FlToSlider, null);
        }

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
        // force: apply even if the range did not change (new outlines after a load, built for all the levels)
        private async Task ApplyFlightLevelsAsync(bool force = false)
        {
            var (from, to) = FlRangeChosen();
            if (_openSectors.Outlines == null || (!force && from == _openSectors.FlFrom && to == _openSectors.FlTo))
                return;
            int version = ++_flVersion;
            var outlines = _openSectors.Outlines;
            Dictionary<string, SectorPlan> plans = await Task.Run(() => PlotOpenSectors.BuildPlans(outlines, from, to));
            if (version != _flVersion)
                return; // the handles moved again meanwhile: that build is applied instead
            _openSectors.SetFlightLevels(from, to, plans);
            if (_simulation == null)
                return;
            if (ShowDensityBox.IsChecked == true)
                _ = ShowDensityAsync(); // the density counts the levels shown only
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
            private static readonly System.Windows.Media.Brush GreenBrush = Frozen(0xD7, 0xEE, 0xDC);
            private static readonly System.Windows.Media.Brush YellowBrush = Frozen(0xF6, 0xE5, 0xB5);
            private static readonly System.Windows.Media.Brush RedBrush = Frozen(0xF3, 0xCF, 0xC9);
            // Text on the pill: a dark shade of the same status, readable on its tint
            private static readonly System.Windows.Media.Brush GreenInk = Frozen(0x25, 0x6B, 0x3B);
            private static readonly System.Windows.Media.Brush YellowInk = Frozen(0x7A, 0x54, 0x00);
            private static readonly System.Windows.Media.Brush RedInk = Frozen(0x9E, 0x2C, 0x1E);

            // Every property announces only a real change: a row of the grid is a slot that shows one sector after another
            // (see SyncSlots), and an unchanged text must not make the grid lay out again
            private string _sector, _acc, _configuration;
            public string Sector { get => _sector; set => Set(ref _sector, value, nameof(Sector)); }
            public string Acc { get => _acc; set => Set(ref _acc, value, nameof(Acc)); }
            public string Configuration { get => _configuration; set => Set(ref _configuration, value, nameof(Configuration)); }

            // Ticked in the list: shown on the map (MainWindow._ticked is the source of truth)
            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set
                {
                    if (_isChecked == value) return;
                    _isChecked = value;
                    Raise(nameof(IsChecked));
                }
            }

            private double? _hourly;
            public double? Hourly                      // nominal capacity per hour, null = no limit
            {
                get => _hourly;
                set
                {
                    if (_hourly == value) return;
                    _hourly = value;
                    Raise(nameof(Hourly)); Raise(nameof(CapacityText)); Raise(nameof(CapacitySort));
                }
            }
            public string CapacityText => Hourly == null ? "none" : Hourly.Value.ToString("0.#", CultureInfo.InvariantCulture);
            public double CapacitySort => Hourly ?? double.MaxValue;

            public int Demand { get; private set; }
            public double Load { get; private set; }  // demand / capacity in the window; -1 = no limit
            public string LoadText => Load < 0 ? "-" : (Load * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
            public System.Windows.Media.Brush StatusBrush { get; private set; }
            public System.Windows.Media.Brush StatusInk { get; private set; }

            public void SetValues(int demand, double? capacityInWindow, CapacityStatus status)
            {
                double load = capacityInWindow == null ? -1 : demand / capacityInWindow.Value;
                var brush = status == CapacityStatus.Red ? RedBrush : status == CapacityStatus.Yellow ? YellowBrush : GreenBrush;
                var ink = status == CapacityStatus.Red ? RedInk : status == CapacityStatus.Yellow ? YellowInk : GreenInk;
                SetValues(demand, load, brush, ink);
            }

            private void SetValues(int demand, double load, System.Windows.Media.Brush brush, System.Windows.Media.Brush ink)
            {
                bool demandChanged = demand != Demand || StatusBrush == null, loadChanged = load != Load || StatusBrush == null;
                bool statusChanged = brush != StatusBrush;
                Demand = demand;
                Load = load;
                StatusBrush = brush;
                StatusInk = ink;
                if (demandChanged) Raise(nameof(Demand));
                if (loadChanged) { Raise(nameof(Load)); Raise(nameof(LoadText)); }
                if (statusChanged) { Raise(nameof(StatusBrush)); Raise(nameof(StatusInk)); }
            }

            // This slot now shows that sector. The name first: setting IsChecked raises the tick box's Checked event, whose
            // handler reads the name of the row
            public void ShowAs(SectorRow s, bool ticked)
            {
                Sector = s.Sector;
                Acc = s.Acc;
                Configuration = s.Configuration;
                Hourly = s.Hourly;
                IsChecked = ticked;
                SetValues(s.Demand, s.Load, s.StatusBrush, s.StatusInk);
            }

            private void Set(ref string field, string value, string property)
            {
                if (field == value) return;
                field = value;
                Raise(property);
            }

            private void Raise(string property) =>
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(property));

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
        private int _demandVersion;
        private (DateTime WindowStart, int DrawVersion, int DemandVersion) _listKey;
        private int _listDrawVersion = -1, _listDemandVersion = -1;
        private bool _gridByCode;             // selection changed by RefreshSectorList, not by the user

        // ROW REUSE. The grid's rows (_sectorRows) are slots: row i always shows the i-th sector of the sorted, filtered
        // list, and when the order changes the slots keep their place and change what they show (a few texts). The data
        // itself is one SectorRow per drawn sector in _sectorData, never bound. Moving rows instead made WPF prepare
        // their containers again (about 200 ms per refresh when sorted by load at 1800x), and the view's live sorting
        // re-placed each changed row on its own while the others still had the old window's values, so a red row
        // could stay under a yellow one. Rows are only added or removed at the end, when the number of sectors changes
        private readonly Dictionary<string, SectorRow> _sectorData = new Dictionary<string, SectorRow>(StringComparer.OrdinalIgnoreCase);
        private string _selectedSector;       // the selection follows the sector, not the slot

        private void InitSectorList()
        {
            SectorGrid.ItemsSource = _sectorRows;
            SectorGrid.SelectionChanged += (s, e) =>
            {
                if (!_gridByCode)
                    _selectedSector = (SectorGrid.SelectedItem as SectorRow)?.Sector;
            };
            SectorGrid.SizeChanged += (s, e) => FitConfigColumn();
        }

        // The last column (configuration) fills what the others leave, as a fixed width set here when the strip is resized.
        // Measured at 1800x (30 s runs, alternated): with the columns exactly as wide as the list (a star width does that)
        // and the horizontal scroll bar on Auto, the bar went on and off at each value change, and every refresh (4 times
        // a second while playing) laid the grid out twice: 26 to 107 pauses over 50 ms in 30 s and up to 127 garbage
        // collections. The horizontal scroll bar is now disabled (MainWindow.xaml) and this width fixed: 4 pauses, about
        // 38 collections, as with the list hidden. The vertical scroll bar's width is always left free
        // The panel that holds the grid's rows (found once, in the grid's template)
        private VirtualizingStackPanel _rowPanel;

        private VirtualizingStackPanel RowPanel()
        {
            if (_rowPanel != null)
                return _rowPanel;
            var todo = new Stack<DependencyObject>();
            todo.Push(SectorGrid);
            while (todo.Count > 0)
            {
                DependencyObject d = todo.Pop();
                if (d is VirtualizingStackPanel panel)
                    return _rowPanel = panel;
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                    todo.Push(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
            }
            return null;
        }

        private void FitConfigColumn()
        {
            if (SectorGrid.Columns.Count == 0 || SectorGrid.ActualWidth <= 0)
                return;
            DataGridColumn config = SectorGrid.Columns[SectorGrid.Columns.Count - 1];
            double others = SectorGrid.Columns.Take(SectorGrid.Columns.Count - 1).Sum(c => c.ActualWidth);
            double width = Math.Max(40, Math.Floor(SectorGrid.ActualWidth - others - SystemParameters.VerticalScrollBarWidth - 2));
            if (Math.Abs(config.ActualWidth - width) >= 1)
                config.Width = new DataGridLength(width);
        }

        private static bool Contains(string text, string part) =>
            text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        // SORTING: a click on a header changes the order of the data (CompareRows), never the view's SortDescriptions
        private string _sortKey = nameof(SectorRow.Sector);   // SortMemberPath of the sorted column (name when none)
        private bool _sortDescending;

        private void SectorGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;   // the grid does not sort the view
            DataGridColumn column = e.Column;
            // First click lowest first, then it alternates (as the grid's own sorting)
            var direction = column.SortDirection == System.ComponentModel.ListSortDirection.Ascending
                ? System.ComponentModel.ListSortDirection.Descending : System.ComponentModel.ListSortDirection.Ascending;
            foreach (DataGridColumn c in SectorGrid.Columns)
                if (c != column) c.SortDirection = null;
            column.SortDirection = direction;
            _sortKey = column.SortMemberPath;
            _sortDescending = direction == System.ComponentModel.ListSortDirection.Descending;
            SyncSlots();
        }

        // Order of two rows for the sorted column; the sector name breaks ties, so the order is total and stable (rows
        // with the same load do not swap at every refresh)
        private int CompareRows(SectorRow a, SectorRow b)
        {
            int c;
            switch (_sortKey)
            {
                case nameof(SectorRow.Load): c = a.Load.CompareTo(b.Load); break;
                case nameof(SectorRow.Demand): c = a.Demand.CompareTo(b.Demand); break;
                case nameof(SectorRow.CapacitySort): c = a.CapacitySort.CompareTo(b.CapacitySort); break;
                case nameof(SectorRow.Acc): c = string.Compare(a.Acc, b.Acc, StringComparison.OrdinalIgnoreCase); break;
                case nameof(SectorRow.Configuration): c = string.Compare(a.Configuration, b.Configuration, StringComparison.OrdinalIgnoreCase); break;
                default: c = 0; break;
            }
            int byName = string.Compare(a.Sector, b.Sector, StringComparison.OrdinalIgnoreCase);
            if (_sortKey == nameof(SectorRow.Sector))
                return _sortDescending ? -byName : byName;
            if (_sortDescending)
                c = -c;
            return c != 0 ? c : byName;
        }

        // Sorted and filtered data into the slots. A slot whose sector and values are the same says nothing, so a refresh
        // where only some loads changed only redraws those cells
        private void SyncSlots()
        {
            if (SectorGrid == null || SectorSearchBox == null)
                return;   // text events while the window is being built
            string q = SectorSearchBox.Text.Trim();
            var shown = _sectorData.Values
                .Where(r => q.Length == 0 || Contains(r.Sector, q) || Contains(r.Acc, q) || Contains(r.Configuration, q))
                .ToList();
            shown.Sort(CompareRows);

            _gridByCode = true;
            while (_sectorRows.Count > shown.Count)
                _sectorRows.RemoveAt(_sectorRows.Count - 1);
            if (_sectorRows.Count < shown.Count)
            {
                while (_sectorRows.Count < shown.Count)
                    _sectorRows.Add(new SectorRow());
                // Rows added (the first rows of a load, or sectors opening): the grid built a row for every item, not
                // only the ones on screen (65 instead of about 21), and kept them, so each refresh laid out three times
                // as many rows (17 to 20 pauses over 50 ms in 30 s at 1800x instead of 3 to 7). Measuring its row panel
                // again, once the rows are in, makes it keep only the rows on screen
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    SectorGrid.InvalidateMeasure();
                    RowPanel()?.InvalidateMeasure();
                }));
            }
            SectorRow selected = null;
            for (int i = 0; i < shown.Count; i++)
            {
                _sectorRows[i].ShowAs(shown[i], IsTicked(shown[i].Sector));
                if (_selectedSector != null && string.Equals(shown[i].Sector, _selectedSector, StringComparison.OrdinalIgnoreCase))
                    selected = _sectorRows[i];
            }
            // The selection stays on its sector (or comes back to it when it opens again)
            if (SectorGrid.SelectedItem != selected)
                SectorGrid.SelectedItem = selected;
            _gridByCode = false;
        }

        private void RefreshSectorList(DateTime time)
        {
            if (_demand == null)
                return;
            var (start, end) = _demand.WindowAt(time);
            var key = (start, _openSectors.DrawVersion, _demandVersion);
            if (key == _listKey)
                return;
            _listKey = key;
            RefreshSectorRows(start, end);
        }

        private void RefreshSectorRows(DateTime start, DateTime end)
        {
            if (_openSectors.DrawVersion != _listDrawVersion || _demandVersion != _listDemandVersion)
            {
                // Other sectors drawn (or a new demand): the sectors that closed go, the new ones come, the others stay
                // (their configuration may have changed)
                var drawn = new Dictionary<string, (string Acc, string Config)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (acc, config, name) in _openSectors.DrawnSectors())
                    drawn[name] = (acc, config);
                foreach (string name in _sectorData.Keys.ToList())
                    if (!drawn.TryGetValue(name, out var now) || now.Acc != _sectorData[name].Acc)
                        _sectorData.Remove(name);
                foreach (var kv in drawn)
                {
                    if (_sectorData.TryGetValue(kv.Key, out SectorRow row))
                        row.Configuration = kv.Value.Config;
                    else
                        _sectorData[kv.Key] = new SectorRow { Sector = kv.Key, Acc = kv.Value.Acc, Configuration = kv.Value.Config,
                                                             Hourly = _demand.HourlyCapacity(kv.Key) };
                }
                _listDrawVersion = _openSectors.DrawVersion;
                _listDemandVersion = _demandVersion;
            }

            // All the values first, then the order
            foreach (SectorRow row in _sectorData.Values)
            {
                int demand = _demand.Demand(row.Sector, start, end);
                double? capacity = _demand.Capacity(row.Sector);
                row.SetValues(demand, capacity, _demand.Status(demand, capacity));
            }
            SyncSlots();
        }

        // The list follows the timeline at most LIST_INTERVAL apart (4 times a second): more is not readable and each
        // refresh re-sorts and lays out the grid. The last time asked for is always shown, LIST_INTERVAL later at most
        private static readonly TimeSpan LIST_INTERVAL = TimeSpan.FromMilliseconds(250);
        private readonly Stopwatch _listClock = Stopwatch.StartNew();
        private TimeSpan _listLastRefresh = TimeSpan.FromDays(-1);   // long ago (MinValue overflowed the subtraction)
        private DateTime _listPendingTime;
        private DispatcherTimer _listTimer;

        private void ScheduleListRefresh(DateTime time)
        {
            _listPendingTime = time;
            TimeSpan since = _listClock.Elapsed - _listLastRefresh;
            if (since >= LIST_INTERVAL)
            {
                _listTimer?.Stop();
                _listLastRefresh = _listClock.Elapsed;
                RefreshSectorList(time);
                return;
            }
            if (_listTimer == null)
            {
                _listTimer = new DispatcherTimer(DispatcherPriority.Background);
                _listTimer.Tick += (s, e) =>
                {
                    _listTimer.Stop();
                    _listLastRefresh = _listClock.Elapsed;
                    RefreshSectorList(_listPendingTime);
                };
            }
            if (!_listTimer.IsEnabled)
            {
                _listTimer.Interval = LIST_INTERVAL - since;
                _listTimer.Start();
            }
        }

        private void SectorSearch_TextChanged(object sender, TextChangedEventArgs e) => SyncSlots();

        // ------------------------------------------------------------------------------------------------
        // TICKS (checkbox column of the list): the ticked sectors are the only ones on the map, with their aircraft; none
        // ticked = all. Two or more open the comparison (ComparePanel) in the right column, beside the strip. A tick stays
        // while its sector is closed (it comes back when the sector opens). Show all / Esc / × of the comparison untick all
        // ------------------------------------------------------------------------------------------------

        private readonly List<string> _ticked = new List<string>();   // in the order ticked = the comparison's columns

        private bool IsTicked(string name) => _ticked.Contains(name, StringComparer.OrdinalIgnoreCase);

        // Mouse, Space or an accessibility tool: all change IsChecked. Changes made by code (_ticked already updated) stop here
        private void SectorCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox box) || !(box.DataContext is SectorRow row))
                return;
            bool on = box.IsChecked == true;
            if (IsTicked(row.Sector) == on)
                return;
            SetTicked(row.Sector, on);
        }

        private void SetTicked(string name, bool on)
        {
            _ticked.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (on)
                _ticked.Add(name);
            foreach (SectorRow r in _sectorRows.Where(r => string.Equals(r.Sector, name, StringComparison.OrdinalIgnoreCase)))
                r.IsChecked = on;
            ApplyTicks();
            // Frame what is ticked, once the comparison has taken its width (the map gets narrower)
            if (_ticked.Count > 0)
                Dispatcher.BeginInvoke(new Action(() => ZoomToSectors(_ticked.ToList())), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void ApplyTicks()
        {
            TickedText.Text = _ticked.Count == 0 ? "" : $"{_ticked.Count} shown";
            UpdateMasterTick();
            if (_ticked.Count == 0)
            {
                ShowOnlySectors(null);
                CloseCompare();
                return;
            }
            if (_ticked.Count == 1)
            {
                // One sector ticked: its full panel, with the strip left open to tick more
                CloseCompare();
                SetFocus(_ticked[0], keepStrip: true);
                ShowOnlySectors(_ticked.ToList());
                return;
            }
            ShowOnlySectors(_ticked.ToList());   // ends any focus: two or more = the comparison
            if (_focusSector == null)
                _ = ShowCompareAsync();
            else
                CloseCompare();
        }

        private void CloseCompare()
        {
            bool wasOpen = ComparePanelView.Visibility == Visibility.Visible;
            ComparePanelView.Visibility = Visibility.Collapsed;
            _comparePeriods = null;
            if (wasOpen)
            {
                DrawOpenPeriods();
                PanelClosed();
            }
        }

        // CAMERA HOME. Whenever the right column ends up empty, however it happened (× or Esc of the sector panel or the
        // comparison, unticking the last sector in the list, the master tick, Show all...), the camera flies back to the
        // whole area. Checked once the action has settled (ContextIdle): a panel closing only to make room for another
        // (the comparison coming back after a sector panel, or the panel of the one sector still ticked) stays where it is.
        // Not while the data is being cleared for a new load, which frames the new area itself
        private bool _homePending;
        private bool _clearingData;

        private void PanelClosed()
        {
            if (_homePending || _clearingData)
                return;
            _homePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                _homePending = false;
                // A panel is still meant to be shown: a sector focused, or two or more ticked (comparison, maybe still
                // being built in the background)
                if (_simulation != null && _focusSector == null && _ticked.Count < 2)
                    FrameArea();
            }));
        }

        private DateTime _compareFrom, _compareTo;

        // The comparison's columns, from the analysis of the whole simulation (built in the background if needed)
        private async Task ShowCompareAsync()
        {
            if (_simulation == null)
                return;
            List<string> names = _ticked.ToList();
            AirspaceTimeline tl = await TimelineAsync();
            if (tl == null || !names.SequenceEqual(_ticked) || _ticked.Count < 2 || _focusSector != null)
                return;
            _compareFrom = tl.Starts[0];
            _compareTo = tl.Starts[tl.Starts.Count - 1] + tl.Step;
            ComparePanelView.SetSectors(names.Select(n => BuildCompareSector(n, tl)).ToList());
            ComparePanelView.Visibility = Visibility.Visible;
            _comparePeriods = AllOpenPeriods(names, tl);
            DrawOpenPeriods();
            UpdateCompareNow(_simTime);
        }

        // COMPARISON > Export: two CSVs in the format of the other exports (the user chose CSV so they work together):
        // comparison_load.csv (one row per sliding step, one load % column per compared sector) and comparison_summary.csv
        // (the sectors.csv columns for the compared sectors). Each starts with the parameter lines and the compared sectors
        private async void ComparePanel_ExportRequested(object sender, EventArgs e)
        {
            if (_simulation == null || _ticked.Count < 2)
                return;
            if (_recomputing)
            {
                ShowNotice("The entries are being computed again: export when they are ready");
                return;
            }
            List<string> names = _ticked.ToList();
            if (!HaveExportFolder())
                return;
            ShowProgress($"Exporting the comparison of {names.Count} sectors...", null);
            try
            {
                AirspaceTimeline tl = await TimelineAsync();
                if (tl == null)
                    throw new InvalidOperationException("the analysis could not be computed");
                var parameters = ExportParameters(_simulation);
                parameters.Insert(1, ("Compared sectors", string.Join(", ", names)));
                // The sectors go in the file names, so two comparisons do not overwrite each other:
                // comparison_load_LEBLF25_LECPGOX.csv (more than four: the first three and "_and_n_more")
                string tag = names.Count <= 4 ? string.Join("_", names) : string.Join("_", names.Take(3)) + $"_and_{names.Count - 3}_more";
                string loadFile = $"comparison_load_{tag}.csv", summaryFile = $"comparison_summary_{tag}.csv";
                string load = System.IO.Path.Combine(ExportsFolder, loadFile);
                string summary = System.IO.Path.Combine(ExportsFolder, summaryFile);
                int rows = await Task.Run(() =>
                {
                    AnalysisExport.ComparisonSummary(tl, names, summary, parameters);
                    return AnalysisExport.ComparisonLoad(tl, names, load, parameters);
                });
                HideProgress($"Saved the comparison of {string.Join(", ", names)}: {loadFile} ({rows:N0} rows) and {summaryFile} in {ExportsFolder}");
                ShowNotice("Comparison exported (2 CSV files)");
            }
            catch (Exception ex) // async void: anything not caught here would close the app
            {
                HideProgress(ex is System.IO.IOException ? $"Could not export the comparison (is its file open in Excel?): {ex.Message}"
                                                          : $"Could not export the comparison: {ex.Message}");
            }
        }

        private CompareSector BuildCompareSector(string name, AirspaceTimeline tl)
        {
            var c = new CompareSector { Name = name };
            int row = tl.RowOf(name);
            if (row < 0)
            {
                c.Acc = "not open in the simulation";
                return c;
            }
            c.Acc = tl.SectorAcc[row];
            int n = tl.Starts.Count, peak = -1, near = 0, over = 0;
            double? cap = tl.Capacity[row];
            for (int k = 0; k < n; k++)
            {
                sbyte s = tl.Status[row, k];
                // Runs of the same status make one rectangle of the day strip
                if (c.Strip.Count > 0 && c.Strip[c.Strip.Count - 1].Status == s)
                    c.Strip[c.Strip.Count - 1] = (c.Strip[c.Strip.Count - 1].From, (k + 1.0) / n, s);
                else
                    c.Strip.Add(((double)k / n, (k + 1.0) / n, s));
                if (s == AirspaceTimeline.CLOSED) continue;
                if (peak < 0 || tl.Demand[row, k] > tl.Demand[row, peak]) peak = k;
                if (s == (sbyte)CapacityStatus.Yellow) near++;
                else if (s == (sbyte)CapacityStatus.Red) over++;
            }
            var inv = CultureInfo.InvariantCulture;
            c.Entries = tl.EntryCount[row].ToString("N0", inv);
            double avg = tl.AverageMinutes[row];
            c.Average = $"{(int)avg} min {Math.Round((avg - (int)avg) * 60):00} s";
            if (peak >= 0)
            {
                int d = tl.Demand[row, peak];
                c.Peak = (cap == null ? $"{d} entries" : (d / cap.Value * 100).ToString("0", inv) + " %") + $" at {tl.Windows[peak].Start:HH:mm}";
            }
            c.Near = AnalysisWindow.Duration(TimeSpan.FromTicks(near * tl.Step.Ticks));
            c.Over = AnalysisWindow.Duration(TimeSpan.FromTicks(over * tl.Step.Ticks));
            return c;
        }

        private void UpdateCompareNow(DateTime t)
        {
            if (_demand == null || _compareTo <= _compareFrom)
                return;
            var (ws, we) = _demand.WindowAt(t);
            var drawn = _openSectors.DrawnSectors();
            var now = new Dictionary<string, CompareNow>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in _ticked)
            {
                var d = drawn.FirstOrDefault(x => string.Equals(x.Sector, name, StringComparison.OrdinalIgnoreCase));
                if (d.Sector == null)
                {
                    now[name] = new CompareNow { Config = "closed", LoadText = "closed", Status = AirspaceTimeline.CLOSED, Demand = "" };
                    continue;
                }
                int demand = _demand.Demand(name, ws, we);
                double? cap = _demand.Capacity(name);
                now[name] = new CompareNow
                {
                    Config = d.Configuration,
                    LoadText = cap == null ? "no limit" : (demand / cap.Value * 100).ToString("0", CultureInfo.InvariantCulture) + " %",
                    Status = (sbyte)_demand.Status(demand, cap),
                    Demand = cap == null ? $"{demand}" : FormattableString.Invariant($"{demand} of {cap.Value:0.#}"),
                };
            }
            ComparePanelView.SetNow(now, (t - _compareFrom).TotalSeconds / (_compareTo - _compareFrom).TotalSeconds);
        }

        private void SectorGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (SectorGrid.SelectedItem is SectorRow row)
                SelectAndZoom(row.Sector);
        }

        private void ShowAll_Click(object sender, RoutedEventArgs e) => ShowAllSectors();

        // Header of the tick column, a master checkbox (Gmail style): with something ticked, untick everything; the panel or
        // comparison that was open closes, so the camera flies home like any panel closing. With nothing ticked it does
        // nothing (ticking every sector would only open a huge comparison)
        private void UntickAll_Click(object sender, RoutedEventArgs e)
        {
            if (_ticked.Count > 0)
                ShowAllSectors();   // the panel or comparison closes and the camera flies home (PanelClosed)
            UpdateMasterTick();   // the click toggled it: back to what the ticks say
        }

        // Empty with nothing ticked, a dash (indeterminate) with some ticked
        private void UpdateMasterTick() => UntickAllBox.IsChecked = _ticked.Count == 0 ? false : (bool?)null;

        private void ShowAllSectors()
        {
            _gridByCode = true;
            SectorGrid.UnselectAll();
            _gridByCode = false;
            _selectedSector = null;
            _ticked.Clear();
            foreach (SectorRow r in _sectorRows)
                r.IsChecked = false;
            TickedText.Text = "";
            UpdateMasterTick();
            CloseCompare();
            ShowOnlySectors(null);
        }

        // names = null: everything again
        private void ShowOnlySectors(ICollection<string> names)
        {
            // Any other choice than the one sector zoomed to ends its focus (marks on the time bar)
            if (names == null || names.Count != 1 || !names.Contains(_focusSector, StringComparer.OrdinalIgnoreCase))
                SetFocus(null);
            _openSectors.ShowOnly(names);
            ShowAllBTN.Visibility = names == null ? Visibility.Collapsed : Visibility.Visible;
            if (_simulation != null && TimelineBar.Visibility == Visibility.Visible)
                SetTime(_simTime, continuous: false); // aircraft of the selected sectors only
        }

        // One sector: selected in the list (scrolled into view), the only one on the map, and zoomed to
        private void SelectAndZoom(string name)
        {
            _selectedSector = name;
            _gridByCode = true;
            SectorRow row = _sectorRows.FirstOrDefault(r => string.Equals(r.Sector, name, StringComparison.OrdinalIgnoreCase));
            SectorGrid.SelectedItem = row;
            if (row != null)
            {
                // After the redraw and layout, otherwise the row is selected but not scrolled to
                Dispatcher.BeginInvoke(new Action(() => SectorGrid.ScrollIntoView(row)), System.Windows.Threading.DispatcherPriority.Background);
            }
            _gridByCode = false;
            SetFocus(name);   // before ShowOnlySectors, which ends any other focus
            ShowOnlySectors(new[] { name });
            ZoomToSector(name);
        }

        // Frames the sector drawn (its extent in the flight levels shown) with a 30 % margin
        private void ZoomToSector(string name)
            => ZoomToSectors(new[] { name });

        // Frames every one of these sectors that is drawn (the union of their extents), with a 30 % margin
        private void ZoomToSectors(IEnumerable<string> names)
        {
            var boxes = names.Select(n => _openSectors.ExtentOf(n)).Where(b => b != null).Select(b => b.Value).ToList();
            if (boxes.Count == 0)
                return;
            double minLat = boxes.Min(b => b.MinLat), minLon = boxes.Min(b => b.MinLon), maxLat = boxes.Max(b => b.MaxLat), maxLon = boxes.Max(b => b.MaxLon);
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
            if (_simulation == null || ShowDensityBox.IsChecked == true)
                return; // with the density shown the sectors are hidden: a double-click just zooms the map
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

        // Space plays / pauses only when the simulation is ready and idle: data loaded, timeline shown, no load or recompute
        // running, no pop-up open, no modifier, not a held key. When the focus is somewhere that uses Space itself (a text
        // box, a button, a tick box, a menu) it keeps doing that; a focused Play button toggles play by itself anyway
        private bool SpaceTogglesPlay(System.Windows.Input.KeyEventArgs e)
        {
            if (e.IsRepeat || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None)
                return false;
            if (_simulation == null || TimelineBar.Visibility != Visibility.Visible || _recomputing || !DefaultDataItem.IsEnabled)
                return false;
            if (ParamsPopup.IsOpen || FilterPopup.IsOpen || SpeedMenu.IsOpen)
                return false;
            object focused = System.Windows.Input.Keyboard.FocusedElement;
            return !(focused is System.Windows.Controls.Primitives.TextBoxBase || focused is PasswordBox
                     || focused is System.Windows.Controls.Primitives.ButtonBase || focused is MenuItem || focused is ComboBox);
        }

        // F11: full screen on / off. Esc: close the parameters pop-up, otherwise the sector panel, otherwise show all the
        // sectors again, otherwise leave full screen
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F11)
            {
                SetFullScreen(!_fullScreen);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Space)
            {
                if (SpaceTogglesPlay(e))
                {
                    PlayBTN_Click(PlayBTN, new RoutedEventArgs());
                    e.Handled = true;
                }
                return;
            }
            if (e.Key != System.Windows.Input.Key.Escape)
                return;
            if (ParamsPopup.IsOpen)
                ParamsPopup.IsOpen = false;
            else if (_focusSector != null)
                SectorPanel_CloseRequested(this, EventArgs.Empty);
            else if (_openSectors.Only != null)
                ShowAllSectors();   // a comparison open closes and the camera flies home (PanelClosed)
            else if (_fullScreen)
                SetFullScreen(false);
        }

        // ------------------------------------------------------------------------------------------------
        // WINDOW FRAME: the Windows caption is replaced by TitleBar (WindowChrome in MainWindow.xaml). Maximised, a window
        // with WindowChrome reaches past the screen by its resize frame, so the content is pushed in by that frame. Full
        // screen = no frame, maximised, over the taskbar; the title bar stays (it holds the menus)
        // ------------------------------------------------------------------------------------------------

        private bool _fullScreen;
        private WindowState _stateBeforeFullScreen = WindowState.Normal;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Windows 11: rounded corners for the frameless window (ignored on Windows 10)
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
            }
            catch (Exception ex) { Debug.WriteLine("[Corners] " + ex.Message); }
            UpdateWindowFrame();
        }

        private void Window_StateChanged(object sender, EventArgs e) => UpdateWindowFrame();

        private void UpdateWindowFrame()
        {
            bool maximised = WindowState == WindowState.Maximized;
            if (maximised && !_fullScreen)
            {
                // Resize frame + padded border, in device pixels, to DIPs
                double scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;
                double frame = (GetSystemMetrics(32 /* SM_CXFRAME */) + GetSystemMetrics(92 /* SM_CXPADDEDBORDER */)) / scale;
                RootGrid.Margin = new Thickness(frame);
            }
            else
                RootGrid.Margin = new Thickness(0);
            WinMaxBTN.Content = maximised ? "" : "";   // restore / maximise
            WinMaxBTN.ToolTip = maximised ? "Restore" : "Maximise";
            System.Windows.Automation.AutomationProperties.SetName(WinMaxBTN, maximised ? "Restore" : "Maximise");
            WinMaxBTN.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        }

        private void SetFullScreen(bool on)
        {
            if (on == _fullScreen)
                return;
            _fullScreen = on;
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (on)
            {
                _stateBeforeFullScreen = WindowState;
                WindowState = WindowState.Normal;          // re-maximising without a frame covers the taskbar
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                chrome.ResizeBorderThickness = new Thickness(0);
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                chrome.ResizeBorderThickness = new Thickness(6);
                WindowState = _stateBeforeFullScreen;
            }
            UpdateWindowFrame();
        }

        private void WinMin_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void WinClose_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
        private void WinMax_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }

        // ------------------------------------------------------------------------------------------------
        // FOCUS on the sector zoomed to (double-click): the left strip is hidden and the SECTOR PANEL (right) shows its
        // data; its open periods (opening scheme of its ACC, consecutive records that keep it open joined) are marked on
        // the time bar. While it is open the map shows only that sector; while it is closed (playback past a closing, or
        // the time bar moved out of its periods) the map shows all the sectors and the panel says so. Playback pauses at a
        // closing unless the panel's "Pause when it closes" is off. Closing the panel (× or Esc) brings back the strip
        // with all the sectors
        // ------------------------------------------------------------------------------------------------

        private string _focusSector;
        private string _focusAcc;
        private List<(DateTime Start, DateTime End)> _focusPeriods;
        private List<SectorEntry> _focusEntries;      // its Level Open entries, by entry time
        private Dictionary<long, Trajectory> _flightsById = new Dictionary<long, Trajectory>(); // relevant flights (aircraft inside)
        private bool _stripWasShown;                  // the strip's state before the panel replaced it

        // name = null ends the focus (panel closed, strip back); the caller chooses what the map shows. keepStrip: the
        // strip stays open beside the panel (a sector ticked in the list, so the user can go on ticking)
        private void SetFocus(string name, bool keepStrip = false)
        {
            if (string.Equals(name, _focusSector, StringComparison.OrdinalIgnoreCase))
                return;
            bool wasFocused = _focusSector != null;
            _focusSector = name;
            _focusAcc = null;
            _focusPeriods = null;
            _focusEntries = null;
            if (name != null && _simulation != null)
            {
                _focusAcc = _openSectors.DrawnSectors().FirstOrDefault(d => string.Equals(d.Sector, name, StringComparison.OrdinalIgnoreCase)).Acc;
                // The opening scheme covers the whole AIRAC month: only the periods within the simulation
                if (_focusAcc != null)
                    _focusPeriods = OpenPeriods(_focusAcc, name).Where(p => p.End > _simulation.Start && p.Start < _simulation.End).ToList();
                _focusEntries = _program.Entries.Where(e => e.Level == EntryLevel.Open && string.Equals(e.Sector, name, StringComparison.OrdinalIgnoreCase))
                                                .OrderBy(e => e.Entry).ToList();
                if (_flightsById.Count != _program.RelevantTrajectories.Count)
                    _flightsById = _program.RelevantTrajectories.GroupBy(f => f.FlightIdentifier).ToDictionary(g => g.Key, g => g.First());
                if (_focusPeriods != null && _focusPeriods.Count > 0)
                    StatusText.Text = $"{name} is open {string.Join(", ", _focusPeriods.Select(p => $"{p.Start:dd/MM HH:mm}-{p.End:HH:mm}"))} UTC " +
                                      "(green on the time bar). × or Esc closes the sector panel.";
            }
            DrawOpenPeriods();

            if (name != null)
            {
                CloseCompare();   // the full panel takes the column; the comparison comes back when it closes
                if (!wasFocused)
                    OpenSectorPanel(keepStrip);
                _ = FillSectorPanelAsync();
            }
            else if (wasFocused)
                CloseSectorPanel();
        }

        // The panel slides in from the right. The strip folds to its narrow "Map »" button, which opens it again beside the
        // panel (unless keepStrip); its state before is restored when the panel closes
        private void OpenSectorPanel(bool keepStrip)
        {
            _stripWasShown = ParamPanel.Visibility == Visibility.Visible;
            if (!keepStrip)
            {
                ParamPanel.Visibility = Visibility.Collapsed;
                ParamShowBTN.Visibility = Visibility.Visible;
            }
            SectorInfoPanel.Visibility = Visibility.Visible;
            var slide = new System.Windows.Media.Animation.DoubleAnimation(SectorInfoPanel.Width, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            var shift = new System.Windows.Media.TranslateTransform();
            SectorInfoPanel.RenderTransform = shift;
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
        }

        private void CloseSectorPanel()
        {
            SectorInfoPanel.Visibility = Visibility.Collapsed;
            ParamPanel.Visibility = _stripWasShown ? Visibility.Visible : Visibility.Collapsed;
            ParamShowBTN.Visibility = _stripWasShown ? Visibility.Collapsed : Visibility.Visible;
            PanelClosed();
        }

        // × of the panel (or Esc): all the sectors again, the whole list, the home view (PanelClosed)
        private void SectorPanel_CloseRequested(object sender, EventArgs e)
        {
            SectorSearchBox.Text = "";
            if (_ticked.Count >= 2)
            {
                // Opened from the comparison: back to the ticked sectors and their comparison, home view as before
                SetFocus(null);
                ApplyTicks();
                FrameArea();
            }
            else
                ShowAllSectors();
        }

        // Click on the panel chart or the analysis window: the timeline goes there (paused)
        private void GoToTime(DateTime t)
        {
            if (_simulation == null || TimelineBar.Visibility != Visibility.Visible)
                return;
            if (t < _simulation.Start) t = _simulation.Start;
            if (t > _simulation.End) t = _simulation.End;
            SetPlaying(false);
            RequestTime(t);
        }

        // The same jump done at once, for a caller that looks at the map right after (e.g. is the sector open then?)
        private void GoToTimeNow(DateTime t)
        {
            if (_simulation == null || TimelineBar.Visibility != Visibility.Visible)
                return;
            if (t < _simulation.Start) t = _simulation.Start;
            if (t > _simulation.End) t = _simulation.End;
            SetPlaying(false);
            SetTime(t, continuous: false);
        }

        // Static part of the panel; the chart waits for the AirspaceTimeline if it is still being built
        private async Task FillSectorPanelAsync()
        {
            string name = _focusSector;
            SectorInfoPanel.SetSector(BuildSectorInfo(name, null));
            if (_simulation != null && TimelineBar.Visibility == Visibility.Visible)
                SectorInfoPanel.SetNow(SectorNowAt(_simTime));
            AirspaceTimeline timeline = await TimelineAsync();
            if (timeline != null && _focusSector == name)
                SectorInfoPanel.SetSector(BuildSectorInfo(name, timeline));
        }

        private SectorInfo BuildSectorInfo(string name, AirspaceTimeline tl)
        {
            var info = new SectorInfo { Name = name, Timeline = tl, Row = tl?.RowOf(name) ?? -1, CapacityPerWindow = _demand.Capacity(name),
                                        OpenPeriods = _focusPeriods ?? new List<(DateTime, DateTime)>() };

            // Elementary sectors it is made of (nested collapsed sectors flattened) and their flight levels
            var members = new List<ElementarySector>();
            void Flatten(string n, HashSet<string> seen)
            {
                if (!seen.Add(n)) return;
                Airspace cs = _program.Collapsed.FirstOrDefault(c => string.Equals(c.Id, n, StringComparison.OrdinalIgnoreCase));
                if (cs != null)
                    foreach (Sector m in cs.Sectors) Flatten(m.Name.Trim(), seen);
                else
                {
                    ElementarySector es = _program.Elementary.FirstOrDefault(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (es != null) members.Add(es);
                }
            }
            Flatten(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            bool collapsed = _program.Collapsed.Any(c => string.Equals(c.Id, name, StringComparison.OrdinalIgnoreCase));
            info.Kind = collapsed ? $"Collapsed sector: {string.Join(" + ", members.Select(m => m.Name))}" : "Elementary sector";
            var volumes = members.SelectMany(m => m.Volumes).ToList();
            string levels = volumes.Count == 0 ? "" : $"FL{volumes.Min(v => v.BottomLevel):000}-{(volumes.Max(v => v.TopLevel) >= SectorPlanBuilder.NO_UPPER_LIMIT ? "UNL" : volumes.Max(v => v.TopLevel).ToString("000"))}";
            double? hourly = _demand.HourlyCapacity(name);
            string capacity = hourly == null ? "no capacity limit"
                            : FormattableString.Invariant($"capacity {hourly.Value:0.#}/h = {info.CapacityPerWindow.Value:0.#} per {_demand.WindowMinutes:0}-min window");
            info.Info = $"ACC {_focusAcc} · {levels} · {capacity}";

            // Figures over the simulation
            var inv = CultureInfo.InvariantCulture;
            List<SectorEntry> entries = _focusEntries ?? new List<SectorEntry>();
            int opened = entries.Count(e => e.Cause == EntryCause.Opened);
            TimeSpan openTime = TimeSpan.FromTicks(info.OpenPeriods.Sum(p => (Min(p.End, _simulation.End) - Max(p.Start, _simulation.Start)).Ticks));
            info.Figures.Add(("Entries", $"{entries.Count:N0}", "Entries into the sector while open, in the whole simulation"));
            info.Figures.Add(("  crossing / opened while inside", $"{entries.Count - opened:N0} / {opened:N0}", "Opened while inside = the flight was already in it when the sector opened"));
            info.Figures.Add(("Open in the simulation", AnalysisWindow.Duration(openTime), "Total time the sector is open between the start and the end of the simulation"));
            if (entries.Count > 0)
            {
                double avg = entries.Average(e => (e.Exit - e.Entry).TotalSeconds);
                info.Figures.Add(("Average time inside", $"{(int)(avg / 60)} min {avg % 60:00} s", "Average duration of its entries (entry to exit)"));
            }
            if (tl != null && info.Row >= 0)
            {
                int peak = -1, near = 0, over = 0;
                for (int k = 0; k < tl.Starts.Count; k++)
                {
                    sbyte s = tl.Status[info.Row, k];
                    if (s == AirspaceTimeline.CLOSED) continue;
                    if (peak < 0 || tl.Demand[info.Row, k] > tl.Demand[info.Row, peak]) peak = k;
                    if (s == (sbyte)CapacityStatus.Yellow) near++;
                    else if (s == (sbyte)CapacityStatus.Red) over++;
                }
                if (peak >= 0)
                {
                    int d = tl.Demand[info.Row, peak];
                    string load = info.CapacityPerWindow == null ? "" : $" ({(d / info.CapacityPerWindow.Value * 100).ToString("0", inv)} %)";
                    info.Figures.Add(("Peak demand", $"{d}{load} at {tl.Windows[peak].Start:HH:mm}-{tl.Windows[peak].End:HH:mm}",
                                      $"Most entries in one window while open ({tl.Windows[peak].Start:dd/MM})"));
                }
                info.Figures.Add(("Time near the limit (yellow)", AnalysisWindow.Duration(TimeSpan.FromTicks(near * tl.Step.Ticks)), "Time the map shows it yellow: 80-100 % of the capacity"));
                info.Figures.Add(("Time overloaded (red)", AnalysisWindow.Duration(TimeSpan.FromTicks(over * tl.Step.Ticks)), "Time the map shows it red: demand above the capacity"));
            }
            AddFlowsAndLevels(info, entries);

            // Aircraft inside at each sample time (entry <= t < exit), drawn as a line over the demand bars
            if (tl != null && info.Row >= 0)
            {
                info.AircraftInside = new int[tl.Starts.Count];
                for (int k = 0; k < tl.Starts.Count; k++)
                {
                    DateTime t = tl.Starts[k];
                    int n = 0;
                    foreach (SectorEntry e in entries)
                    {
                        if (e.Entry > t) break; // sorted by entry
                        if (e.Exit > t) n++;
                    }
                    info.AircraftInside[k] = n;
                }
            }
            return info;
        }

        // ------------------------------------------------------------------------------------------------
        // SECTOR PANEL > Export: everything about the sector in one Excel workbook, one sheet each: summary (with the
        // parameters), demand over time, entries, flows, flight levels. Exports/sector_<NAME>.xlsx
        // ------------------------------------------------------------------------------------------------
        private async void SectorPanel_ExportRequested(object sender, EventArgs e)
        {
            SectorInfo info = SectorInfoPanel.Info;
            if (info == null || _simulation == null)
                return;
            if (_recomputing)
            {
                ShowNotice("The entries are being computed again: export when they are ready");
                return;
            }
            if (!HaveExportFolder())
                return;
            ShowProgress($"Exporting {info.Name} to Excel...", null);
            try
            {
                if (info.Timeline == null)
                {
                    AirspaceTimeline tl = await TimelineAsync();
                    if (tl == null)
                        throw new InvalidOperationException("the analysis could not be computed");
                    info = BuildSectorInfo(info.Name, tl);
                }
                var parameters = ExportParameters(_simulation);
                List<SectorEntry> entries = (_focusEntries ?? new List<SectorEntry>()).ToList();
                string path = System.IO.Path.Combine(ExportsFolder, $"sector_{info.Name}.xlsx");
                await Task.Run(() => XlsxWriter.Write(path, SectorSheets(info, entries, parameters)));
                HideProgress($"Saved {info.Name} (summary, demand over time, {entries.Count:N0} entries, flows, flight levels) to {path}");
                ShowNotice($"{info.Name} exported to Excel");
            }
            catch (Exception ex) // async void: anything not caught here would close the app
            {
                HideProgress(ex is System.IO.IOException ? $"Could not export the sector (is its file open in Excel?): {ex.Message}"
                                                          : $"Could not export the sector: {ex.Message}");
            }
        }

        private static List<XlsxSheet> SectorSheets(SectorInfo info, List<SectorEntry> entries, List<(string, string)> parameters)
        {
            var summary = new XlsxSheet { Name = "Summary", Header = { "Item", "Value", "Note" } };
            summary.Rows.Add(new object[] { "Sector", info.Name, info.Kind });
            summary.Rows.Add(new object[] { "Data", info.Info, null });
            foreach (var p in info.OpenPeriods)
                summary.Rows.Add(new object[] { "Open period", $"{p.Start:dd/MM/yyyy HH:mm} - {p.End:dd/MM/yyyy HH:mm} UTC", null });
            foreach (var f in info.Figures)
                summary.Rows.Add(new object[] { f.Label.Trim(), f.Value, f.Tip });
            foreach (var (name, value) in parameters)
                summary.Rows.Add(new object[] { name, value, "parameter" });

            var demand = new XlsxSheet { Name = "Demand over time",
                Header = { "Time (UTC)", "Window start", "Window end", "Open", "Entries", "Capacity per window", "Load (%)", "Status", "Aircraft inside" } };
            AirspaceTimeline tl = info.Timeline;
            if (tl != null && info.Row >= 0)
                for (int k = 0; k < tl.Starts.Count; k++)
                {
                    sbyte s = tl.Status[info.Row, k];
                    bool open = s != AirspaceTimeline.CLOSED;
                    int d = tl.Demand[info.Row, k];
                    object load = open && info.CapacityPerWindow != null ? (object)(d / info.CapacityPerWindow.Value * 100) : null;
                    string status = !open ? "closed" : s == (sbyte)CapacityStatus.Red ? "overloaded" : s == (sbyte)CapacityStatus.Yellow ? "near the limit" : "below 80 %";
                    demand.Rows.Add(new object[] { tl.Starts[k], tl.Windows[k].Start, tl.Windows[k].End, open ? "yes" : "no",
                                                   open ? (object)d : null, info.CapacityPerWindow, load, status, info.AircraftInside?[k] });
                }

            var list = new XlsxSheet { Name = "Entries",
                Header = { "Flight id", "Call sign", "Entry (UTC)", "Exit (UTC)", "Duration (s)", "Cause", "ACC", "Configuration" } };
            foreach (SectorEntry e in entries)
                list.Rows.Add(new object[] { e.FlightId, e.CallSign, e.Entry, e.Exit, (int)Math.Round((e.Exit - e.Entry).TotalSeconds),
                                             e.Cause == EntryCause.Opened ? "opened while inside" : "crossing", e.Acc, e.Configuration });

            var flows = new XlsxSheet { Name = "Flows", Header = { "Direction", "Sector", "Flights", "Share (%)" } };
            void AddFlows(string direction, List<(string Name, int Count)> items)
            {
                int total = items.Sum(i => i.Count);
                foreach (var (name, count) in items)
                    flows.Rows.Add(new object[] { direction, name, count, total == 0 ? 0.0 : count * 100.0 / total });
            }
            AddFlows("comes from", info.FromAll);
            AddFlows("goes to", info.ToAll);

            var levels = new XlsxSheet { Name = "Flight levels",
                Header = { "From FL", "To FL", "Climbing (aircraft-min)", "Level (aircraft-min)", "Descending (aircraft-min)", "Total (aircraft-min)" } };
            foreach (var l in info.Levels)
                levels.Rows.Add(new object[] { l.Fl, l.Fl + SectorInfo.LEVEL_BAND, l.Climb, l.Level, l.Descend, l.Climb + l.Level + l.Descend });

            return new List<XlsxSheet> { summary, demand, list, flows, levels };
        }

        // A flight's previous / next open sector counts if it left / entered it within this time of the entry / exit
        private static readonly TimeSpan FLOW_GAP = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan PROFILE_SAMPLE = TimeSpan.FromSeconds(60);

        private Dictionary<long, List<SectorEntry>> _openByFlight;
        private List<SectorEntry> _openByFlightSource;

        // Flows (previous / next sector of each entry, top 5 each way) and the flight-level profile of the sector
        private void AddFlowsAndLevels(SectorInfo info, List<SectorEntry> entries)
        {
            if (_openByFlightSource != _program.Entries)
            {
                _openByFlightSource = _program.Entries;
                _openByFlight = _program.Entries.Where(e => e.Level == EntryLevel.Open).GroupBy(e => e.FlightId)
                                                .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Entry).ToList());
            }
            var from = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var to = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Count(Dictionary<string, int> d, string key) => d[key] = d.TryGetValue(key, out int n) ? n + 1 : 1;
            bool ClosesAt(DateTime t) => info.OpenPeriods.Any(p => p.End == t);

            foreach (SectorEntry e in entries)
            {
                if (!_openByFlight.TryGetValue(e.FlightId, out List<SectorEntry> stays))
                    continue;
                var others = stays.Where(s => !string.Equals(s.Sector, info.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (e.Cause == EntryCause.Crossing)
                {
                    SectorEntry prev = others.Where(s => s.Exit <= e.Entry + TimeSpan.FromSeconds(1) && s.Exit >= e.Entry - FLOW_GAP)
                                             .OrderByDescending(s => s.Exit).FirstOrDefault();
                    Count(from, prev?.Sector ?? "outside the ACCs");
                }
                if (!ClosesAt(e.Exit))
                {
                    SectorEntry next = others.Where(s => s.Entry >= e.Exit - TimeSpan.FromSeconds(1) && s.Entry <= e.Exit + FLOW_GAP)
                                             .OrderBy(s => s.Entry).FirstOrDefault();
                    Count(to, next?.Sector ?? "outside the ACCs");
                }
            }
            info.FromAll = from.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
            info.ToAll = to.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
            info.From = info.FromAll.Take(5).ToList();
            info.To = info.ToAll.Take(5).ToList();

            // Profile: every entry sampled each PROFILE_SAMPLE; trend from the levels 30 s before and after
            var bands = new SortedDictionary<int, (double Climb, double Level, double Descend)>();
            double perSample = PROFILE_SAMPLE.TotalMinutes;
            foreach (SectorEntry e in entries)
            {
                if (!_flightsById.TryGetValue(e.FlightId, out Trajectory flight))
                    continue;
                for (DateTime t = e.Entry; t < e.Exit; t += PROFILE_SAMPLE)
                {
                    if (!flight.TryGetPosition(t, out _, out _, out double fl))
                        continue;
                    flight.TryGetPosition(t - TimeSpan.FromSeconds(30), out _, out _, out double before);
                    flight.TryGetPosition(t + TimeSpan.FromSeconds(30), out _, out _, out double after);
                    int band = (int)(fl / SectorInfo.LEVEL_BAND) * SectorInfo.LEVEL_BAND;
                    bands.TryGetValue(band, out var b);
                    double change = after - before;   // flight levels per minute
                    if (change > 1) b.Climb += perSample;
                    else if (change < -1) b.Descend += perSample;
                    else b.Level += perSample;
                    bands[band] = b;
                }
            }
            info.Levels = bands.Select(kv => (kv.Key, kv.Value.Climb, kv.Value.Level, kv.Value.Descend)).ToList();
        }

        // Live part of the panel at time t: configuration, window and load, aircraft inside, next entries
        private SectorNow SectorNowAt(DateTime t)
        {
            string name = _focusSector;
            var now = new SectorNow { Time = t };
            var drawn = _openSectors.DrawnSectors().FirstOrDefault(d => string.Equals(d.Sector, name, StringComparison.OrdinalIgnoreCase));
            var (ws, we) = _demand.WindowAt(t);
            if (drawn.Sector == null)
            {
                DateTime? opens = _focusPeriods?.Where(p => p.Start > t).Select(p => (DateTime?)p.Start).FirstOrDefault();
                now.Title = $"{t:dd/MM HH:mm} UTC · closed now" + (opens != null ? $" (opens {opens.Value:dd/MM HH:mm})" : "");
                now.Load = "closed";
                now.Status = AirspaceTimeline.CLOSED;
                now.Demand = "Not in the open configuration of its ACC";
            }
            else
            {
                int demand = _demand.Demand(name, ws, we);
                double? capacity = _demand.Capacity(name);
                now.Title = $"{t:dd/MM HH:mm} UTC · {drawn.Configuration} · window {ws:HH:mm}-{we:HH:mm}";
                now.Load = capacity == null ? "no limit" : (demand / capacity.Value * 100).ToString("0", CultureInfo.InvariantCulture) + " %";
                now.Status = (sbyte)_demand.Status(demand, capacity);
                now.Demand = capacity == null ? $"{demand} entries" : FormattableString.Invariant($"{demand} entries / {capacity.Value:0.#} capacity");
            }

            // Inside now: the flights with an entry into this sector around t (entry <= t < exit)
            if (_focusEntries != null)
            {
                foreach (SectorEntry e in _focusEntries)
                {
                    if (e.Entry > t) break;
                    if (t < e.Exit)
                    {
                        double fl = 0;
                        if (_flightsById.TryGetValue(e.FlightId, out Trajectory flight))
                            flight.TryGetPosition(t, out _, out _, out fl);
                        now.Inside.Add($"{e.CallSign,-8} FL{fl:000}  since {e.Entry:HH:mm}");
                    }
                }
                int first = _focusEntries.FindIndex(e => e.Entry > t);
                if (first >= 0)
                    foreach (SectorEntry e in _focusEntries.Skip(first).Take(5))
                        now.Next.Add($"{e.Entry:HH:mm:ss}  {e.CallSign}");
            }
            return now;
        }

        private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
        private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

        // While a sector is focused, the map shows it alone (zoomed to it) when it is open, and all the sectors from the
        // home view when it is closed
        private void FollowFocusOnMap()
        {
            if (_focusSector == null)
                return;
            bool open = _openSectors.IsDrawn(_focusSector);
            bool alone = _openSectors.Only != null;
            if (open && !alone)
            {
                _openSectors.ShowOnly(new[] { _focusSector });
                ZoomToSector(_focusSector);
            }
            else if (!open && alone)
            {
                _openSectors.ShowOnly(null);
                FrameArea();
            }
        }

        // Periods [start, end) in which the ACC has a configuration with the sector open, consecutive ones joined
        private List<(DateTime Start, DateTime End)> OpenPeriods(string acc, string sector)
        {
            var periods = new List<(DateTime Start, DateTime End)>();
            foreach (OpeningSchemeRecord r in _program.OpeningScheme.GetRecords(acc))
            {
                Configuration cfg = _program.FindConfiguration(acc, r.ConfigurationName);
                bool open = cfg != null && (cfg.Elementary.Any(es => string.Equals(es.Name, sector, StringComparison.OrdinalIgnoreCase)) ||
                                            cfg.Collapsed.Any(cs => string.Equals(cs.Id, sector, StringComparison.OrdinalIgnoreCase)));
                if (!open)
                    continue;
                if (periods.Count > 0 && r.Start <= periods[periods.Count - 1].End)
                    periods[periods.Count - 1] = (periods[periods.Count - 1].Start, r.End > periods[periods.Count - 1].End ? r.End : periods[periods.Count - 1].End);
                else
                    periods.Add((r.Start, r.End));
            }
            return periods;
        }

        // Half the width of the slider thumb: the track (and the thumb centre) runs this far inside the slider
        private const double SLIDER_END_PX = 7;

        // Periods marked on the time bar: the focused sector's open periods, or, while comparing, the periods when all the
        // compared sectors are open at once (null = no marks)
        private List<(DateTime Start, DateTime End)> _comparePeriods;
        private List<(DateTime Start, DateTime End)> MarkedPeriods => _focusPeriods ?? (ComparePanelView.Visibility == Visibility.Visible ? _comparePeriods : null);

        // Intersection of the open periods of every compared sector, within the simulation
        private List<(DateTime Start, DateTime End)> AllOpenPeriods(IList<string> names, AirspaceTimeline tl)
        {
            List<(DateTime Start, DateTime End)> all = null;
            foreach (string name in names)
            {
                int row = tl.RowOf(name);
                if (row < 0)
                    return new List<(DateTime, DateTime)>();   // never open in the simulation: never all open
                var mine = OpenPeriods(tl.SectorAcc[row], name);
                if (all == null) { all = mine; continue; }
                var both = new List<(DateTime Start, DateTime End)>();
                foreach (var a in all)
                    foreach (var b in mine)
                    {
                        DateTime s = a.Start > b.Start ? a.Start : b.Start, e = a.End < b.End ? a.End : b.End;
                        if (e > s) both.Add((s, e));
                    }
                all = both.OrderBy(p => p.Start).ToList();
            }
            return (all ?? new List<(DateTime, DateTime)>()).Where(p => p.End > _simulation.Start && p.Start < _simulation.End).ToList();
        }

        // Green band under the track for each marked period (MarkedPeriods), a dark tick at each opening and closing
        private void DrawOpenPeriods()
        {
            OpenPeriodMarks.Children.Clear();
            var periods = MarkedPeriods;
            if (periods == null || _simulation == null)
                return;
            double total = (_simulation.End - _simulation.Start).TotalSeconds;
            double width = OpenPeriodMarks.ActualWidth - 2 * SLIDER_END_PX, height = OpenPeriodMarks.ActualHeight;
            if (total <= 0 || width <= 0)
                return;
            double X(DateTime t) => SLIDER_END_PX + Math.Max(0, Math.Min(1, (t - _simulation.Start).TotalSeconds / total)) * width;
            void Add(double x, double y, double w, double h, System.Windows.Media.Brush fill)
            {
                var r = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = fill };
                Canvas.SetLeft(r, x);
                Canvas.SetTop(r, y);
                OpenPeriodMarks.Children.Add(r);
            }
            var band = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x6E, 0x6B));
            var tick = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0E, 0x3B, 0x39));
            foreach (var (start, end) in periods)
            {
                if (end <= _simulation.Start || start >= _simulation.End)
                    continue;
                // Under the track only (it is 4 px high in the middle), so the played part and the thumb stay clean
                double x0 = X(start), x1 = X(end), below = height / 2 + 4;
                // A tick at both ends, also when the period starts before the simulation (about half the sectors, e.g.
                // LECMSAN 27/07 04:45) or ends after it: drawn at the end of the bar then, so every band has its two ends
                Add(x0, height - 4, Math.Max(2, x1 - x0), 3, band);
                Add(Math.Max(0, x0 - 1), below, 2, height - below, tick);
                Add(Math.Min(OpenPeriodMarks.ActualWidth - 2, x1 - 1), below, 2, height - below, tick);
            }
        }

        private void OpenPeriodMarks_SizeChanged(object sender, SizeChangedEventArgs e) => DrawOpenPeriods();

        // End of a marked period in (from, to], if playback passes it: the focused sector closes, or one of the compared
        // sectors does
        // The pause at a closing stops this long before it, while the sector is still open (see Timer_Tick)
        private static readonly TimeSpan PAUSE_BEFORE_CLOSING = TimeSpan.FromSeconds(1);
        private DateTime? _pausedBeforeClosing;   // the closing playback last paused before: Play goes through it

        private DateTime? FocusClosesBetween(DateTime from, DateTime to)
        {
            var periods = MarkedPeriods;
            if (periods == null)
                return null;
            foreach (var (_, end) in periods)
                if (end > from && end <= to)
                    return end;
            return null;
        }

        // Short notice at the top of the map: fades in, stays about 3 s, fades out
        private void ShowNotice(string text)
        {
            NoticeText.Text = text;
            var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250))));
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.2))));
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4))));
            NoticeBox.BeginAnimation(OpacityProperty, fade);
        }

        // Map filter: aircraft dots, entry markers and exit markers, each on or off; the filter icon turns blue while
        // something is hidden
        private void ShowLayers_Changed(object sender, RoutedEventArgs e)
        {
            if (_aircraft == null)
                return;
            _aircraft.ShowAircraft = ShowAircraftBox.IsChecked == true;
            _aircraft.ShowEntries = ShowEntriesBox.IsChecked == true;
            _aircraft.ShowExits = ShowExitsBox.IsChecked == true;
            bool filtering = !(_aircraft.ShowAircraft && _aircraft.ShowEntries && _aircraft.ShowExits);
            MapFilterIcon.Foreground = filtering ? Charts.Cursor : System.Windows.Media.Brushes.Black;
            MapFilterBTN.ToolTip = filtering ? "Some layers are hidden: click to choose" : "Show or hide aircraft, entries and exits";
        }

        // ------------------------------------------------------------------------------------------------
        // TRAFFIC DENSITY (Map panel, "Sectors | Traffic density" switch): where the traffic of the whole simulation flows,
        // aircraft-minutes per 0.05° cell (2D) and per 0.1° x FL30 box (3D) in the flight levels shown (TrafficDensity,
        // background). The sectors are hidden meanwhile so the map is readable; aircraft and markers follow the map filter
        // ------------------------------------------------------------------------------------------------

        private PlotDensity _density;
        private int _densityVersion;

        private void ShowDensity_Changed(object sender, RoutedEventArgs e)
        {
            if (_density == null)
                return;
            if (ShowDensityBox.IsChecked == true)
                _ = ShowDensityAsync();
            else
            {
                _densityVersion++;
                _density.Clear();
                DensityLegend.Visibility = Visibility.Collapsed;
                _openSectors.IsVisible = true;
                HideProgress("Traffic density hidden; sectors shown again."); // also ends a computation still running
            }
        }

        private async Task ShowDensityAsync()
        {
            if (_simulation == null || _program.Area == null)
            {
                SectorsLayerBTN.IsChecked = true;
                ShowNotice("Load the data first: the traffic density is computed from the simulation.");
                return;
            }
            int version = ++_densityVersion;
            TimeSimulation simulation = _simulation;
            int from = _openSectors.FlFrom, to = _openSectors.FlTo;
            ShowProgress("Computing the traffic density of the whole simulation...", null);
            SpatialReference sceneSr = MySceneView.SpatialReference ?? SpatialReferences.Wgs84;
            SpatialReference mapSr = MyMapView.SpatialReference ?? SpatialReferences.WebMercator;
            TrafficDensity density;
            Esri.ArcGISRuntime.UI.ImageFrame sceneFrame = null, mapFrame = null;
            var watch = Stopwatch.StartNew();
            try
            {
                density = await Task.Run(() => new TrafficDensity(simulation, _program.Area, from, to));
                if (density.Total > 0)
                    (sceneFrame, mapFrame) = await Task.Run(() => (PlotDensity.Render(density, sceneSr), PlotDensity.Render(density, mapSr)));
            }
            catch (Exception ex)
            {
                if (version == _densityVersion)
                {
                    SectorsLayerBTN.IsChecked = true;
                    HideProgress("Could not compute the traffic density: " + ex.Message);
                }
                return;
            }
            if (version != _densityVersion || ShowDensityBox.IsChecked != true)
            {
                // Switched back to the sectors (or a newer computation started) meanwhile
                if (version == _densityVersion)
                    HideProgress("Traffic density hidden; sectors shown again.");
                return;
            }
            if (density.Total == 0)
            {
                _density.Clear();
                _openSectors.IsVisible = false;
                DensityLegend.Visibility = Visibility.Collapsed;
                HideProgress("No traffic in the flight levels shown: widen the range under Flight levels shown.");
                return;
            }
            _density.Show(sceneFrame, mapFrame);
            _openSectors.IsVisible = false;
            DensityLegend.Visibility = Visibility.Visible;
            DensityMax.Text = "most (busiest 3 %)";
            string levels = from == 0 && to >= SectorPlanBuilder.NO_UPPER_LIMIT ? "all levels" : $"FL{from:000}-{(to >= SectorPlanBuilder.NO_UPPER_LIMIT ? "UNL" : to.ToString("000"))}";
            Debug.WriteLine($"[Density] {watch.ElapsedMilliseconds} ms, {density.Rows} x {density.Cols} cells");
            HideProgress($"Traffic density of the whole simulation ({levels}): {density.Total / 60:N0} aircraft-hours on a 0.02° grid, smoothed " +
                         $"(computed in {watch.Elapsed.TotalSeconds:0.0} s). Sectors hidden: choose Sectors in the Map panel to see them again.");
        }

        // ------------------------------------------------------------------------------------------------
        // ANALYSIS (toolbar menu): AirspaceTimeline of the whole simulation (built in the background with the demand) shown
        // in the analysis window: demand heat map and traffic over time. A click there moves the timeline (and zooms to the
        // sector clicked in the heat map)
        // ------------------------------------------------------------------------------------------------

        private Task<AirspaceTimeline> _timelineTask;
        private AnalysisWindow _analysis;

        private void StartTimeline()
        {
            TimeSimulation simulation = _simulation;
            SectorDemand demand = _demand;
            _timelineTask = Task.Run(() => new AirspaceTimeline(_program, simulation, demand));
            _ = RefreshTimelineViewsAsync();
        }

        private async Task<AirspaceTimeline> TimelineAsync()
        {
            if (_timelineTask == null)
                return null;
            try { return await _timelineTask; }
            catch (Exception ex) { Debug.WriteLine("[Timeline] " + ex); return null; }
        }

        // A new timeline (new simulation, window or step): the open views follow
        private async Task RefreshTimelineViewsAsync()
        {
            Task<AirspaceTimeline> task = _timelineTask;
            AirspaceTimeline timeline = await TimelineAsync();
            if (timeline == null || task != _timelineTask)
                return;
            _analysis?.SetTimeline(timeline);
            if (_focusSector != null)
                _ = FillSectorPanelAsync();
            if (ComparePanelView.Visibility == Visibility.Visible)
                _ = ShowCompareAsync();
        }

        // Toolbar Analysis: the analysis window (its views are tabs), brought to the front if already open
        private void Analysis_Click(object sender, RoutedEventArgs e) => _ = ShowAnalysisAsync(null);

        // tab: the tab to show, null = the first one when opening, the one shown when already open
        private async Task ShowAnalysisAsync(int? tab)
        {
            if (_analysis == null)
            {
                _analysis = new AnalysisWindow { Owner = this };
                _analysis.Closed += (s, e) => _analysis = null;
                _analysis.JumpRequested += Analysis_JumpRequested;
                _analysis.ScrubRequested += GoToTime;   // dragging the traffic chart's line; the analysis window keeps the focus
                _analysis.Show();
                StatusText.Text = "Preparing the analysis...";
                AirspaceTimeline timeline = await TimelineAsync();
                if (_analysis == null)
                    return;
                if (timeline != null)
                    _analysis.SetTimeline(timeline);
                _analysis.SetTime(_simTime < _simulation.Start ? _simulation.Start : _simTime);
                StatusText.Text = "Analysis: click a time to move the timeline there.";
            }
            if (tab != null)
                _analysis.ShowTab(tab.Value);
            _analysis.Activate();
        }

        private void Analysis_JumpRequested(DateTime time, string sector)
        {
            if (TimelineBar.Visibility != Visibility.Visible)
                ShowTimeline();
            GoToTimeNow(time);
            if (sector != null && _openSectors.IsDrawn(sector))
                SelectAndZoom(sector);
            Activate();
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
            // Icon (Segoe MDL2: play, pause, replay) and the same word for screen readers and tests
            PlayIcon.Text = playing ? "" : atEnd ? "" : "";
            PlayIcon.Margin = new Thickness(playing || atEnd ? 0 : 2, 0, 0, 0); // the play triangle looks centred a bit to the right
            System.Windows.Automation.AutomationProperties.SetName(PlayBTN, playing ? "Pause" : atEnd ? "Replay" : "Play");
            _lastReal = _clock.Elapsed.TotalSeconds;

            // While playing, the time advances once per screen frame (see Frame_Rendering); paused, the 50 ms timer is enough
            System.Windows.Media.CompositionTarget.Rendering -= Frame_Rendering;
            if (playing)
                System.Windows.Media.CompositionTarget.Rendering += Frame_Rendering;
            // Fewer long garbage-collection stops while the map is moving
            System.Runtime.GCSettings.LatencyMode = playing ? System.Runtime.GCLatencyMode.SustainedLowLatency
                                                            : System.Runtime.GCLatencyMode.Interactive;
            SetFullSpeed(playing);
        }

        // POWER THROTTLING. On battery, Windows 11 slows down a window that is not in focus (another window clicked, the
        // map watched beside another program) or covered: playback at 1800x fell from about 33 to 8 updates a second.
        // While playing, the process asks to run at full speed (high quality of service); paused, Windows decides again
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private const int PROCESS_POWER_THROTTLING = 4;              // ProcessPowerThrottling
        private const uint POWER_THROTTLING_EXECUTION_SPEED = 1;

        private static void SetFullSpeed(bool on)
        {
            try
            {
                // Control the execution speed with the state off = never throttled; no control = the system's heuristics
                var state = new PowerThrottlingState { Version = 1, ControlMask = on ? POWER_THROTTLING_EXECUTION_SPEED : 0, StateMask = 0 };
                SetProcessInformation(GetCurrentProcess(), PROCESS_POWER_THROTTLING, ref state,
                                      System.Runtime.InteropServices.Marshal.SizeOf<PowerThrottlingState>());
            }
            catch (Exception ex) { Debug.WriteLine("[SetFullSpeed] " + ex.Message); }   // older Windows: nothing to do
        }

        // PLAYBACK CLOCK. The 50 ms timer gave about 16 updates a second (Windows rounds it to 62 ms), which looks jerky
        // at high speeds: at 1800x each update moved the aircraft two simulated minutes. Playing, the time advances at
        // every frame WPF draws (the screen's rate) instead. Frames run before mouse and keyboard input, so an update
        // waits at least as long as the previous one took: the window always has half of the time for everything else
        private TimeSpan _lastFrame;
        private double _nextAdvance;   // _clock seconds before which no update runs

        private void Frame_Rendering(object sender, EventArgs e)
        {
            // The event can come more than once for the same frame
            TimeSpan frame = ((System.Windows.Media.RenderingEventArgs)e).RenderingTime;
            if (frame == _lastFrame || !_playing || _simulation == null)
                return;
            _lastFrame = frame;
            double start = _clock.Elapsed.TotalSeconds;
            if (start < _nextAdvance)
                return;
            Advance();
            double end = _clock.Elapsed.TotalSeconds;
            _nextAdvance = end + (end - start);
        }

        private void PlayBTN_Click(object sender, RoutedEventArgs e)
        {
            if (_simulation == null)
                return;
            if (!_playing && _simTime >= _simulation.End)
                SetTime(_simulation.Start, continuous: false); // restart from the beginning
            SetPlaying(!_playing);
        }

        // Speed pill: opens the list of speeds above it
        private void SpeedBTN_Click(object sender, RoutedEventArgs e)
        {
            SpeedMenu.PlacementTarget = SpeedBTN;
            SpeedMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            SpeedMenu.IsOpen = true;
        }

        private void SpeedItem_Click(object sender, RoutedEventArgs e)
        {
            var chosen = (MenuItem)sender;
            _speed = (int)chosen.Tag;
            foreach (MenuItem item in SpeedMenu.Items)
                item.IsChecked = item == chosen;
            SpeedText.Text = _speed + "x";
        }

        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderByCode || _simulation == null)
                return;
            RequestTime(_simulation.Start.AddSeconds(e.NewValue));
        }

        // A jump asked for by the time bar or a chart drag. The mouse moves much more often than the map can be redrawn,
        // and running every move made them queue up (the bar lagged behind the mouse and then caught up in jerks). Here
        // the moves are merged: one update runs once input and drawing are done, with the latest time asked for
        private DateTime _requestedTime;
        private bool _timeRequestPending;

        private void RequestTime(DateTime t)
        {
            _requestedTime = t;
            if (_timeRequestPending)
                return;
            _timeRequestPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _timeRequestPending = false;
                if (_simulation != null)
                    SetTime(_requestedTime, continuous: false);
            }));
        }

        // Runs always (also paused) so markers disappear after MARKER_LIFE of real time; moves the time only when playing
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!_playing)
                Advance();   // paused: only the markers expire. Playing, Frame_Rendering advances the time
        }

        private void Advance()
        {
            double now = _clock.Elapsed.TotalSeconds;
            // At most a quarter of a second per update: frames stop while the window is minimised, and the time would
            // otherwise jump by the whole pause when it comes back
            double realSeconds = Math.Min(now - _lastReal, 0.25);
            _lastReal = now;

            if (_playing)
            {
                DateTime next = _simTime.AddSeconds(realSeconds * _speed);
                bool finished = next >= _simulation.End;
                if (finished)
                    next = _simulation.End;

                // The sector of the panel closes: pause there (default), or keep playing with all the sectors on the map
                // until it opens again (FollowFocusOnMap). The panel stays either way.
                // The pause is PAUSE_BEFORE_CLOSING before the closing: at the closing time itself the sector already counts
                // as closed, so the map showed all the sectors and the camera flew home, the opposite of what the pause
                // is for. The closing paused at is remembered, so Play then goes through it instead of pausing again
                DateTime? closes = FocusClosesBetween(_simTime, next);
                bool passing = closes != null && closes == _pausedBeforeClosing;
                if (closes != null)
                    _pausedBeforeClosing = null;
                DateTime pauseAt = closes == null ? next : Max(_simTime, closes.Value - PAUSE_BEFORE_CLOSING);
                if (closes != null && _focusSector == null)
                {
                    // Comparing: one of the compared sectors closes, so they are no longer all open
                    if (ComparePanelView.PauseOnClose && !passing)
                    {
                        SetTime(pauseAt, continuous: true);
                        SetPlaying(false);
                        _pausedBeforeClosing = closes;
                        ShowNotice($"One of the compared sectors closes at {closes.Value:HH:mm} UTC · paused");
                        return;
                    }
                    ShowNotice($"Not all the compared sectors are open from {closes.Value:HH:mm} UTC");
                }
                else if (closes != null)
                {
                    string name = _focusSector;
                    if (SectorInfoPanel.PauseOnClose && !passing)
                    {
                        SetTime(pauseAt, continuous: true);
                        SetPlaying(false);
                        _pausedBeforeClosing = closes;
                        ShowNotice($"{name} closes at {closes.Value:HH:mm} UTC · paused");
                        return;
                    }
                    ShowNotice($"{name} closed at {closes.Value:HH:mm} UTC · showing all sectors");
                    StatusText.Text = $"{name} closed at {closes.Value:dd/MM HH:mm} UTC; all the sectors are shown until it opens again.";
                }

                SetTime(next, continuous: true);
                if (finished)
                {
                    // Tell the user why it stopped and where the results are
                    SetPlaying(false);
                    int entries = _program.Entries.Count(x => x.Level == EntryLevel.Open);
                    ShowNotice($"Simulation finished at {_simulation.End:dd/MM HH:mm} UTC: {_simulation.FlightCount:N0} flights, " +
                               $"{entries:N0} sector entries. Results > Export entries saves them.");
                }
            }
            _aircraft.RemoveExpiredMarkers();
        }

        // continuous = playing forward: markers for the entries/exits passed since the last update. A jump clears them
        private void SetTime(DateTime time, bool continuous)
        {
            // Sectors at that time first: the focused sector alone while open, all the sectors while it is closed
            _openSectors.Update(time);
            FollowFocusOnMap();

            // Aircraft and markers only between the flight levels shown (display filter)
            int flFrom = _openSectors.FlFrom, flTo = _openSectors.FlTo;
            bool Shown(double fl) => fl >= flFrom && (fl < flTo || flTo >= SectorPlanBuilder.NO_UPPER_LIMIT);
            // With sectors selected (list or double-click), only the aircraft inside them and their markers
            IReadOnlyCollection<string> only = _openSectors.Only;
            bool Selected(IEnumerable<string> sectors) => only == null || sectors.Any(s => only.Contains(s));
            if (continuous && time > _simTime)
                _aircraft.AddMarkers(_simulation.EventsBetween(_simTime, time).Where(ev => Shown(ev.FL) && Selected(new[] { ev.Sector })).ToList(), MarkerLife());
            else if (!continuous)
                _aircraft.ClearMarkers();
            // A jump to another time: the closing playback paused before no longer counts as "already paused at", so
            // playing through it again pauses again. Not when the time is the same (a redraw while paused there)
            if (!continuous && time != _simTime)
                _pausedBeforeClosing = null;
            _simTime = time;

            // Map colours at every update; the sector list at most 4 times a second (ScheduleListRefresh)
            var (ws, we) = _demand.WindowAt(time);
            _openSectors.Recolor(name => _demand.Status(_demand.Demand(name, ws, we), _demand.Capacity(name)));
            ScheduleListRefresh(time);
            if (_focusSector != null && SectorInfoPanel.Visibility == Visibility.Visible)
                SectorInfoPanel.SetNow(SectorNowAt(time));
            if (ComparePanelView.Visibility == Visibility.Visible)
                UpdateCompareNow(time);
            _analysis?.SetTime(time);
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
            // Also reached with Enter, which does not care that the Apply button is disabled: while the entries are being
            // computed (load, Recompute) the parameters must not change, the computation reads them flight by flight
            if (!ApplyParamBTN.IsEnabled)
            {
                ParamMessage.Text = "Wait until the entries are computed, then apply.";
                return;
            }
            RecomputeRow.Visibility = Visibility.Collapsed;
            if (!ReadParameter(MinTimeBox, "Minimum time in sector", 0, MAX_SECONDS, "s", out double minTime) ||
                !ReadParameter(ReentryBox, "Reentry time", 0, MAX_SECONDS, "s", out double reentry) ||
                !ReadParameter(WindowBox, "Integration window", 1, MAX_MINUTES, "min", out double window) ||
                !ReadParameter(StepBox, "Sliding step", 1, MAX_MINUTES, "min", out double step))
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

        // Upper limits of the parameters: one hour (the capacities of the file are per hour). Without them a huge number (1e400 reads as infinity) was stored and
        // then failed every time the demand was rebuilt
        private const double MAX_SECONDS = 3600, MAX_MINUTES = 60;

        // Number between min and max in the box, or a message in the panel
        private bool ReadParameter(TextBox box, string name, double min, double max, string unit, out double value)
        {
            string text = box.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= min && value <= max)
                return true;
            ParamMessage.Text = $"{name}: write a number between {min} and {max:0} {unit}.";
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
            // No export meanwhile: its parameter lines would give the new times with the data of the old entries
            ApplyParamBTN.IsEnabled = ExportEntriesItem.IsEnabled = ResultsBar.IsEnabled = false;
            DefaultDataItem.IsEnabled = OwnFilesItem.IsEnabled = false; // no other data set while this one is being computed
            _recomputing = true;
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
                if (ShowDensityBox.IsChecked == true)
                    _ = ShowDensityAsync(); // it was drawn from the previous simulation
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                HideProgress("Could not compute the entries: " + ex.Message);
                ParamMessage.Text = "Could not compute the entries: " + ex.Message;
            }
            finally
            {
                ApplyParamBTN.IsEnabled = ExportEntriesItem.IsEnabled = ResultsBar.IsEnabled = true;
                DefaultDataItem.IsEnabled = OwnFilesItem.IsEnabled = true;
                _recomputing = false;
            }
        }

        // Entries being computed again (Recompute entries): the exports of the panels wait, like the Results menu
        private bool _recomputing;

        // ------------------------------------------------------------------------------------------------
        // WIDTH of the side panels by dragging their inner edge (StripGrip, PanelGrip, CompareGrip). Not narrower than the
        // design width (everything fits there) and not wider than the content can use: the strip only gains room for the
        // configuration name (last column), the right panel for its charts. The map always keeps MAP_MIN px.
        // The timeline bar follows by itself (it fills what the strip leaves)
        // ------------------------------------------------------------------------------------------------
        private const double STRIP_MIN = 390, STRIP_MAX = 480, PANEL_MIN = 340, PANEL_MAX = 640, MAP_MIN = 320;

        private double RightPanelWidth =>
            SectorInfoPanel.Visibility == Visibility.Visible || ComparePanelView.Visibility == Visibility.Visible ? SectorInfoPanel.Width : 0;

        private void StripGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double room = RootGrid.ActualWidth - RightPanelWidth - MAP_MIN;
            ParamPanel.Width = Math.Max(STRIP_MIN, Math.Min(Math.Min(STRIP_MAX, room), ParamPanel.Width + e.HorizontalChange));
        }

        private void StripGrip_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ParamPanel.Width = STRIP_MIN;

        // Sector panel and comparison share the column and the width
        private void PanelGrip_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            double strip = ParamPanel.Visibility == Visibility.Visible ? ParamPanel.Width : ParamShowBTN.ActualWidth;
            double room = RootGrid.ActualWidth - strip - MAP_MIN;
            SetRightPanelWidth(Math.Max(PANEL_MIN, Math.Min(Math.Min(PANEL_MAX, room), SectorInfoPanel.Width - e.HorizontalChange)));
        }

        private void PanelGrip_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => SetRightPanelWidth(PANEL_MIN);

        private void SetRightPanelWidth(double width)
        {
            SectorInfoPanel.Width = width;
            ComparePanelView.Width = width;
        }
    }
}
