using Esri.ArcGISRuntime.Mapping;
using Projecte1_WPF_.ArcGisMethods;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;
using System.Diagnostics;
using System.Drawing;
using System.Windows;

namespace Projecte1_WPF_
{
    public partial class MainWindow : Window
    {
        // TEST: configuration to draw = ACC + configuration name of the .cfg (e.g. "LEBLTMA" + "2AE", "EBBUCTA" + "C3EW1").
        // Use "" in TEST_CONFIG to take the first configuration of the .cfg.
        private const string TEST_ACC = "LEBLTMA";
        private const string TEST_CONFIG = "9AW";

        // Elementary and collapsed sectors are drawn by two plotters (one overlay each): independent, never merged together
        private readonly IAirspacePlotter _elementaryPlotter;
        private readonly IAirspacePlotter _collapsedPlotter;

        public MainWindow(mainProgram program)
        {
            InitializeComponent();
            MySceneView.Scene = new Scene(BasemapStyle.ArcGISImagery);
            _elementaryPlotter = new PlotAirspaces3D(MySceneView, Color.FromArgb(120, 0, 120, 255)); // blue
            _collapsedPlotter = new PlotAirspaces3D(MySceneView, Color.FromArgb(120, 255, 140, 0));  // orange

            Loaded += async (s, e) =>
            {
                try
                {
                    string acc = TEST_ACC;
                    string config = TEST_CONFIG;
                    if (config == "")
                    {
                        acc = program.Configurations[0].AccName;
                        config = program.Configurations[0].ConfigurationName;
                    }

                    Configuration cfg = program.FindConfiguration(acc, config);
                    if (cfg == null)
                    {
                        MessageBox.Show($"Configuration '{acc};{config}' not found in the .cfg.", "TEST error");
                        return;
                    }

                    // Own outline per sector: nothing is fused between elementary sectors, collapsed sectors, or across the two
                    var elementaryOutlines = new List<MergedAirblocks>();
                    for (int i = 0; i < cfg.Elementary.Count; i++)
                        elementaryOutlines.AddRange(AirblockMerger.Merge(cfg.Elementary[i], program.Airblocks));

                    var collapsedOutlines = new List<MergedCollapsedSector>();
                    for (int i = 0; i < cfg.Collapsed.Count; i++)
                        collapsedOutlines.AddRange(CollapsedSectorMerger.Merge(cfg.Collapsed[i], program.Elementary, program.Airblocks));

                    // zoomTo only on the first call so the camera frames what is drawn first;
                    // each plotter only removes its own graphics, so the two groups never overwrite each other
                    await _elementaryPlotter.PlotAsync(elementaryOutlines, zoomTo: collapsedOutlines.Count == 0);
                    await _collapsedPlotter.PlotAsync(collapsedOutlines);

                    string msg = $"{acc};{config}: {cfg.Elementary.Count + cfg.Collapsed.Count + cfg.NotFound.Count} sectors in the .cfg\n" +
                                 $"Elementary: {cfg.Elementary.Count} ({elementaryOutlines.Count} outlines, blue)\n" +
                                 $"Collapsed: {cfg.Collapsed.Count} ({collapsedOutlines.Count} outlines, orange)\n" +
                                 $"Not found: {cfg.NotFound.Count}" +
                                 (cfg.NotFound.Count > 0 ? " -> " + string.Join(", ", cfg.NotFound) : "");
                    Debug.WriteLine("[TEST] " + msg.Replace("\n", " | "));
                    MessageBox.Show(msg, "TEST configuration");
                }
                catch (System.Exception ex)
                {
                    MessageBox.Show(ex.ToString(), "TEST error");
                }
            };
        }
    }
}