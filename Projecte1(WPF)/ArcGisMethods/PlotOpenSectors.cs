using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_.ArcGisMethods
{
    /// <summary>
    /// Draws, for every ACC, the sectors of the configuration the opening scheme has open at a time, each with its name
    /// label, coloured by demand vs capacity (Recolor). Update(t) only redraws the ACCs whose configuration changed.
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

            // Plan view (2D map) of every sector above, elementary or collapsed, by name, for the flight levels shown
            public Dictionary<string, SectorPlan> Plans = new Dictionary<string, SectorPlan>(StringComparer.OrdinalIgnoreCase);

            // What the plans are built from, kept to rebuild them for another flight-level range (BuildPlans): every
            // sector with its leaf elementary members, and each member prepared once
            public readonly List<(string Name, List<string> Members)> PlanInputs = new List<(string Name, List<string> Members)>();
            public readonly Dictionary<string, PlanMember> PlanMembers = new Dictionary<string, PlanMember>(StringComparer.OrdinalIgnoreCase);
        }

        // Flight levels shown [FlFrom, FlTo) (display filter: entries and demand always use the whole airspace)
        public int FlFrom { get; private set; } = 0;
        public int FlTo { get; private set; } = SectorPlanBuilder.NO_UPPER_LIMIT;

        private readonly PlotAirspaces3D _elementary;
        private readonly PlotAirspaces3D _collapsed;
        private readonly mainProgram _program;
        private SectorOutlines _outlines;

        // ACC -> configuration drawn now (null = none open), and the sector names drawn for it
        private readonly Dictionary<string, string> _drawnConfig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _drawnSectors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public int OpenSectorCount { get; private set; }

        // Status of every drawn sector (Recolor); counts for the timeline bar
        private readonly Dictionary<string, CapacityStatus> _status = new Dictionary<string, CapacityStatus>(StringComparer.OrdinalIgnoreCase);
        public int NearLimitCount { get; private set; }
        public int OverloadedCount { get; private set; }

        // Fill = demand against capacity (green, yellow, red); the plan lines on top give the outline and inner borders
        private static readonly List<(string, Color)> STATUS_COLORS = new List<(string, Color)>
        {
            // Alpha (0-255) per face. Seen from above the map is under the top and bottom face of every stacked prism (often
            // 4 faces), so the opacity adds up: keep it low. Red a bit stronger so overloads stand out
            (CapacityStatus.Green.ToString(), Color.FromArgb(40, 0, 170, 0)),
            (CapacityStatus.Yellow.ToString(), Color.FromArgb(50, 240, 200, 0)),
            (CapacityStatus.Red.ToString(), Color.FromArgb(65, 220, 20, 20)),
        };

        // 2D map: one flat fill per sector, so it can be a bit stronger than a 3D face (stacked sectors still add up)
        private static readonly List<(string, Color)> STATUS_COLORS_2D = new List<(string, Color)>
        {
            (CapacityStatus.Green.ToString(), Color.FromArgb(60, 0, 170, 0)),
            (CapacityStatus.Yellow.ToString(), Color.FromArgb(80, 240, 200, 0)),
            (CapacityStatus.Red.ToString(), Color.FromArgb(100, 220, 20, 20)),
        };

        // Edges of the 3D prisms: faint black, so they only hint the volume and do not double the black outline. The sector
        // outline and the inner borders are the plan lines drawn on the top of each prism (_plan3D), the same lines as in 2D
        private static readonly Color PRISM_EDGE = Color.FromArgb(60, 0, 0, 0);


        // Both views are kept up to date, so switching between them is instant; the labels move to the view shown
        public PlotOpenSectors(SceneView sceneView, MapView mapView, mainProgram program)
        {
            _program = program;
            _sceneView = sceneView;
            _mapView = mapView;
            _elementary = new PlotAirspaces3D(sceneView, STATUS_COLORS, PRISM_EDGE);
            _collapsed = new PlotAirspaces3D(sceneView, STATUS_COLORS, PRISM_EDGE);
            _plan3D = new PlotSectorPlan(sceneView, null, threeD: true);
            _plan2D = new PlotSectorPlan(mapView, STATUS_COLORS_2D, threeD: false);
            _labels = new PlotSectorLabels(sceneView); // added after the prisms and lines so the names draw on top
        }

        private readonly SceneView _sceneView;
        private readonly MapView _mapView;
        private readonly PlotSectorPlan _plan3D;  // lines on top of the prisms
        private readonly PlotSectorPlan _plan2D;  // fills and lines of the flat map
        private readonly PlotSectorLabels _labels;

        // Labels to the view shown (2D map or 3D scene)
        public void ShowIn2D(bool twoD)
        {
            _labels.MoveTo(twoD ? _mapView : _sceneView);
        }

        // Both views: white dashed borders between the elementary sectors of a collapsed sector
        public bool ShowMemberBorders
        {
            get => _plan2D.ShowMemberBorders;
            set { _plan2D.ShowMemberBorders = value; _plan3D.ShowMemberBorders = value; }
        }

        // 2D map only (the 3D plan never draws them): black dotted height steps
        public bool ShowLevelSteps
        {
            get => _plan2D.ShowLevelSteps;
            set => _plan2D.ShowLevelSteps = value;
        }

        // Colours every drawn sector with status(name); only the sectors whose status changed are touched.
        // Call after Update(t), which may have drawn new sectors (drawn green)
        public void Recolor(Func<string, CapacityStatus> status)
        {
            var drawn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<string> names in _drawnSectors.Values)
                drawn.UnionWith(names);
            foreach (string name in new List<string>(_status.Keys))
                if (!drawn.Contains(name))
                    _status.Remove(name); // removed with its configuration; drawn green again if it comes back

            NearLimitCount = OverloadedCount = 0;
            foreach (string name in drawn)
            {
                CapacityStatus s = status(name);
                if (!_status.TryGetValue(name, out CapacityStatus old) || old != s)
                {
                    _elementary.SetStatus(name, s.ToString());
                    _collapsed.SetStatus(name, s.ToString());
                    _plan2D.SetStatus(name, s.ToString());
                    _status[name] = s;
                }
                if (s == CapacityStatus.Yellow) NearLimitCount++;
                else if (s == CapacityStatus.Red) OverloadedCount++;
            }
        }

        // NTS unions of every sector used by the loaded configurations, for the 3D prisms and the 2D plan.
        // Slow (seconds): run it off the UI thread
        public static SectorOutlines BuildOutlines(mainProgram p)
        {
            var o = new SectorOutlines();
            var elementaryByName = new Dictionary<string, ElementarySector>(StringComparer.OrdinalIgnoreCase);
            foreach (ElementarySector es in p.Elementary) elementaryByName[es.Name] = es;
            var collapsedById = new Dictionary<string, Airspace>(StringComparer.OrdinalIgnoreCase);
            foreach (Airspace cs in p.Collapsed) collapsedById[cs.Id] = cs;
            // 3D outlines, and the leaf elementary members of every sector for the plans. Each sector once, in the order
            // of the configurations; the unions in parallel (the mergers only read the loaded data)
            var planInputs = o.PlanInputs;
            var elementary = new List<ElementarySector>();
            var collapsed = new List<Airspace>();
            var seenE = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenC = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Configuration c in p.Configurations)
            {
                foreach (ElementarySector es in c.Elementary)
                    if (seenE.Add(es.Name))
                    {
                        elementary.Add(es);
                        planInputs.Add((es.Name, new List<string> { es.Name }));
                    }
                foreach (Airspace cs in c.Collapsed)
                    if (seenC.Add(cs.Id))
                    {
                        collapsed.Add(cs);
                        planInputs.Add((cs.Id, LeafMembers(cs, collapsedById, elementaryByName)));
                    }
            }
            var mergedE = new List<IMergedSector>[elementary.Count];
            var mergedC = new List<IMergedSector>[collapsed.Count];
            System.Threading.Tasks.Parallel.For(0, elementary.Count + collapsed.Count, i =>
            {
                if (i < elementary.Count)
                    mergedE[i] = new List<IMergedSector>(AirblockMerger.Merge(elementary[i], p.Airblocks));
                else
                    mergedC[i - elementary.Count] = new List<IMergedSector>(CollapsedSectorMerger.Merge(collapsed[i - elementary.Count], p.Elementary, p.Airblocks));
            });
            for (int i = 0; i < elementary.Count; i++) o.Elementary[elementary[i].Name] = mergedE[i];
            for (int i = 0; i < collapsed.Count; i++) o.Collapsed[collapsed[i].Id] = mergedC[i];

            // Plans, in parallel: first each elementary sector once (simplified bands and their union, shared and never
            // modified afterwards), then each plan, which only reads them and builds new geometries
            List<string> used = planInputs.SelectMany(x => x.Members).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var prepared = new PlanMember[used.Count];
            System.Threading.Tasks.Parallel.For(0, used.Count, i =>
                prepared[i] = SectorPlanBuilder.Prepare(AirblockMerger.MergeGeometries(elementaryByName[used[i]], p.Airblocks)));
            for (int i = 0; i < used.Count; i++)
                o.PlanMembers[used[i]] = prepared[i];

            o.Plans = BuildPlans(o, 0, SectorPlanBuilder.NO_UPPER_LIMIT);
            return o;
        }

        // Plans of every sector for the flight levels [flFrom, flTo), in parallel (under a second). Background thread
        public static Dictionary<string, SectorPlan> BuildPlans(SectorOutlines o, int flFrom, int flTo)
        {
            var inputs = o.PlanInputs;
            var plans = new SectorPlan[inputs.Count];
            System.Threading.Tasks.Parallel.For(0, inputs.Count, i =>
                plans[i] = SectorPlanBuilder.Build(inputs[i].Name, inputs[i].Members.Select(m => o.PlanMembers[m]).ToList(), flFrom, flTo));
            var result = new Dictionary<string, SectorPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (SectorPlan plan in plans)
                result[plan.Name] = plan;
            return result;
        }

        // New flight-level range shown, with its plans (BuildPlans): everything is redrawn at the next Update
        public void SetFlightLevels(int flFrom, int flTo, Dictionary<string, SectorPlan> plans)
        {
            FlFrom = flFrom;
            FlTo = flTo;
            _outlines.Plans = plans;
            Clear();
        }

        // A band of a sector cut to the flight levels shown
        private class CutBand : IMergedSector
        {
            public string Name { get; set; }
            public int BottomLevel { get; set; }
            public int TopLevel { get; set; }
            public List<List<(double Latitude, double Longitude)>> OuterRings { get; set; }
        }

        // The bands of a sector inside [FlFrom, FlTo), cut to it; empty if the sector is entirely outside
        private List<IMergedSector> InRange(List<IMergedSector> bands)
        {
            if (FlFrom <= 0 && FlTo >= SectorPlanBuilder.NO_UPPER_LIMIT)
                return bands;
            return bands.Where(b => b.BottomLevel < FlTo && b.TopLevel > FlFrom)
                        .Select(b => (IMergedSector)new CutBand
                        {
                            Name = b.Name,
                            BottomLevel = Math.Max(b.BottomLevel, FlFrom),
                            TopLevel = Math.Min(b.TopLevel, FlTo),
                            OuterRings = b.OuterRings
                        }).ToList();
        }

        // Elementary sectors a collapsed sector is made of, following nested collapsed sectors
        private static List<string> LeafMembers(Airspace cs, Dictionary<string, Airspace> collapsedById,
                                                Dictionary<string, ElementarySector> elementaryByName)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Walk(Airspace a)
            {
                if (!visited.Add(a.Id))
                    return;
                foreach (Sector m in a.Sectors)
                {
                    string name = m.Name.Trim();
                    if (collapsedById.TryGetValue(name, out Airspace inner)) Walk(inner);
                    else if (elementaryByName.ContainsKey(name) && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
                }
            }
            Walk(cs);
            return result;
        }

        public SectorOutlines Outlines { get { return _outlines; } }

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
                        _plan2D.Remove(name);
                        _plan3D.Remove(name);
                        _labels.Remove(name);
                    }
                    OpenSectorCount -= old.Count;
                }

                var names = new List<string>();
                Configuration cfg = config == null ? null : _program.FindConfiguration(acc, config);
                if (cfg != null)
                {
                    var elementary = new List<IMergedSector>();
                    var collapsed = new List<IMergedSector>();
                    // One label per sector of the configuration (a collapsed sector: its own name, not its members'), and
                    // its plan lines (flat in 2D, on the top of the prisms in 3D). Bands cut to the flight levels shown;
                    // a sector entirely outside them is not drawn
                    foreach (ElementarySector es in cfg.Elementary)
                        if (_outlines.Elementary.TryGetValue(es.Name, out List<IMergedSector> all) && InRange(all) is var list && list.Count > 0)
                        { elementary.AddRange(list); names.Add(es.Name); _labels.Add(es.Name, list); AddPlan(es.Name, list); }
                    foreach (Airspace cs in cfg.Collapsed)
                        if (_outlines.Collapsed.TryGetValue(cs.Id, out List<IMergedSector> all) && InRange(all) is var list && list.Count > 0)
                        { collapsed.AddRange(list); names.Add(cs.Id); _labels.Add(cs.Id, list); AddPlan(cs.Id, list); }

                    // Without zoom PlotAsync completes synchronously
                    _ = _elementary.PlotAsync(elementary, zoomTo: false);
                    _ = _collapsed.PlotAsync(collapsed, zoomTo: false);
                }

                // Redrawn sectors start green: forget their status so Recolor sets it again
                if (old != null)
                    foreach (string name in old) _status.Remove(name);
                foreach (string name in names) _status.Remove(name);

                _drawnConfig[acc] = config;
                _drawnSectors[acc] = names;
                OpenSectorCount += names.Count;
                DrawVersion++;

                // A selection on the map also applies to the sectors drawn now
                if (_only != null)
                    foreach (string name in names)
                        SetVisible(name, _only.Contains(name));
            }
        }

        // Changes every time the drawn sectors change (configuration change, flight levels, clear): the sector list is
        // rebuilt then
        public int DrawVersion { get; private set; }

        // Sectors drawn now, with the ACC and configuration that have them open
        public List<(string Acc, string Configuration, string Sector)> DrawnSectors()
        {
            var result = new List<(string, string, string)>();
            foreach (KeyValuePair<string, List<string>> kv in _drawnSectors)
            {
                _drawnConfig.TryGetValue(kv.Key, out string config);
                foreach (string name in kv.Value)
                    result.Add((kv.Key, config, name));
            }
            return result;
        }

        public bool IsDrawn(string name) => _drawnSectors.Values.Any(list => list.Contains(name, StringComparer.OrdinalIgnoreCase));

        // SELECTION: only these sectors stay visible (null = all). Kept across redraws (Update applies it to new sectors)
        private HashSet<string> _only;
        public IReadOnlyCollection<string> Only => _only;

        public void ShowOnly(IEnumerable<string> names)
        {
            _only = names == null ? null : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            foreach (List<string> list in _drawnSectors.Values)
                foreach (string name in list)
                    SetVisible(name, _only == null || _only.Contains(name));
        }

        private void SetVisible(string name, bool visible)
        {
            _elementary.SetVisible(name, visible);
            _collapsed.SetVisible(name, visible);
            _plan2D.SetVisible(name, visible);
            _plan3D.SetVisible(name, visible);
            _labels.SetVisible(name, visible);
        }

        // Lat/lon box of a drawn sector (its bands in the levels shown), to zoom to it; null if not drawn
        public (double MinLat, double MinLon, double MaxLat, double MaxLon)? ExtentOf(string name)
        {
            List<IMergedSector> bands = null;
            if (!(_outlines?.Elementary.TryGetValue(name, out bands) == true) && !(_outlines?.Collapsed.TryGetValue(name, out bands) == true))
                return null;
            var points = InRange(bands).SelectMany(b => b.OuterRings).SelectMany(r => r).ToList();
            if (points.Count == 0)
                return null;
            return (points.Min(p => p.Latitude), points.Min(p => p.Longitude), points.Max(p => p.Latitude), points.Max(p => p.Longitude));
        }

        // bands: the sector's bands drawn (cut to the levels shown); the 3D lines go on their prisms
        private void AddPlan(string name, List<IMergedSector> bands)
        {
            if (!_outlines.Plans.TryGetValue(name, out SectorPlan plan))
                return;
            _plan2D.Add(plan, null);
            _plan3D.Add(plan, bands);
        }

        public bool IsVisible
        {
            get => _elementary.IsVisible;
            set
            {
                _elementary.IsVisible = value;
                _collapsed.IsVisible = value;
                _labels.IsVisible = value;
                _plan2D.IsVisible = value;
                _plan3D.IsVisible = value;
            }
        }

        public void Clear()
        {
            _elementary.Clear();
            _collapsed.Clear();
            _plan2D.Clear();
            _plan3D.Clear();
            _labels.Clear();
            _drawnConfig.Clear();
            _drawnSectors.Clear();
            _status.Clear();
            OpenSectorCount = NearLimitCount = OverloadedCount = 0;
            DrawVersion++;
        }
    }
}
