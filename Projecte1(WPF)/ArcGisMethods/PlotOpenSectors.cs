using System;
using System.Collections.Generic;
using System.Drawing;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_.ArcGisMethods
{
    /// <summary>
    /// Draws, for every ACC, the sectors of the configuration the opening scheme has open at a time: elementary sectors in
    /// blue, collapsed sectors in orange. Update(t) only redraws the ACCs whose configuration changed.
    /// </summary>
    public class PlotOpenSectors
    {
        // Outline of every sector that appears in a configuration, built once (BuildOutlines, background thread)
        public class SectorOutlines
        {
            public readonly Dictionary<string, List<IMergedSector>> Elementary =
                new Dictionary<string, List<IMergedSector>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, List<IMergedSector>> Collapsed =
                new Dictionary<string, List<IMergedSector>>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly PlotAirspaces3D _elementary;
        private readonly PlotAirspaces3D _collapsed;
        private readonly mainProgram _program;
        private SectorOutlines _outlines;

        // ACC -> configuration drawn now (null = none open), and the sector names drawn for it
        private readonly Dictionary<string, string> _drawnConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _drawnSectors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int OpenSectorCount { get; private set; }

        public PlotOpenSectors(SceneView sceneView, mainProgram program)
        {
            _program = program;
            _elementary = new PlotAirspaces3D(sceneView, Color.FromArgb(120, 0, 120, 255)); // blue
            _collapsed = new PlotAirspaces3D(sceneView, Color.FromArgb(120, 255, 140, 0));  // orange
        }

        // NTS unions of every sector used by the loaded configurations. Slow (seconds): run it off the UI thread
        public static SectorOutlines BuildOutlines(mainProgram p)
        {
            var o = new SectorOutlines();
            foreach (Configuration c in p.Configurations)
            {
                foreach (ElementarySector es in c.Elementary)
                    if (!o.Elementary.ContainsKey(es.Name))
                        o.Elementary[es.Name] = new List<IMergedSector>(AirblockMerger.Merge(es, p.Airblocks));
                foreach (Airspace cs in c.Collapsed)
                    if (!o.Collapsed.ContainsKey(cs.Id))
                        o.Collapsed[cs.Id] = new List<IMergedSector>(CollapsedSectorMerger.Merge(cs, p.Elementary, p.Airblocks));
            }
            return o;
        }

        public void SetOutlines(SectorOutlines outlines)
        {
            _outlines = outlines;
            Clear();
        }

        public void Update(DateTime time)
        {
            if (_outlines == null || _program.Accs == null)
                return;

            foreach (string acc in _program.Accs)
            {
                string config = _program.OpeningScheme.GetConfigurationName(acc, time);
                _drawnConfig.TryGetValue(acc, out string drawn);
                if (string.Equals(config, drawn, StringComparison.OrdinalIgnoreCase) && _drawnConfig.ContainsKey(acc))
                    continue;

                // Configuration changed: remove the old sectors of this ACC and draw the new ones
                if (_drawnSectors.TryGetValue(acc, out List<string> old))
                {
                    foreach (string name in old)
                    {
                        _elementary.Remove(name);
                        _collapsed.Remove(name);
                    }
                    OpenSectorCount -= old.Count;
                }

                var names = new List<string>();
                Configuration cfg = config == null ? null : _program.FindConfiguration(acc, config);
                if (cfg != null)
                {
                    var elementary = new List<IMergedSector>();
                    var collapsed = new List<IMergedSector>();
                    foreach (ElementarySector es in cfg.Elementary)
                        if (_outlines.Elementary.TryGetValue(es.Name, out List<IMergedSector> list)) { elementary.AddRange(list); names.Add(es.Name); }
                    foreach (Airspace cs in cfg.Collapsed)
                        if (_outlines.Collapsed.TryGetValue(cs.Id, out List<IMergedSector> list)) { collapsed.AddRange(list); names.Add(cs.Id); }

                    // Without zoom PlotAsync completes synchronously
                    _ = _elementary.PlotAsync(elementary, zoomTo: false);
                    _ = _collapsed.PlotAsync(collapsed, zoomTo: false);
                }

                _drawnConfig[acc] = config;
                _drawnSectors[acc] = names;
                OpenSectorCount += names.Count;
            }
        }

        public bool IsVisible
        {
            get => _elementary.IsVisible;
            set { _elementary.IsVisible = value; _collapsed.IsVisible = value; }
        }

        public void Clear()
        {
            _elementary.Clear();
            _collapsed.Clear();
            _drawnConfig.Clear();
            _drawnSectors.Clear();
            OpenSectorCount = 0;
        }
    }
}
