using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_.ArcGisMethods
{
    // Lines of the plan (SectorPlan) of the open sectors: the sector outline as a solid black line, the borders between the
    // elementary sectors of a collapsed sector as white dashes, the height steps as black dots. Each line style is its own
    // overlay so it can be shown or hidden.
    //   2D map:   with a flat fill per sector coloured by demand vs capacity (SetStatus), and the height steps
    //   3D scene: without fill (the prisms are the fill) and without height steps (the volumes show the heights); the
    //             outline on the top of each prism (band), the member borders at the level where the members meet
    public class PlotSectorPlan
    {
        // 3D lines this far above the top face of the prism, so the face does not hide them
        private const double ABOVE_TOP_M = 50;

        // 3D lines get a vertex at least every this distance, so they follow the curved top face like the prisms
        // (GlobeHelper.Densify)
        private const double MAX_SEGMENT_KM = 25;

        private readonly GraphicsOverlay _fills;        // null in 3D
        private readonly GraphicsOverlay _levelSteps;
        private readonly GraphicsOverlay _memberBorders;
        private readonly GraphicsOverlay _outlines;
        private readonly bool _threeD;
        private readonly string _defaultStatus;


        // fills: status -> fill colour (the first one is the colour of a newly drawn sector), or null for lines only.
        // threeD: lines at the height of the sector top (absolute Z) instead of flat
        public PlotSectorPlan(GeoView view, IList<(string Status, Color Fill)> fills, bool threeD)
        {
            _threeD = threeD;
            if (fills != null)
            {
                _defaultStatus = fills[0].Status;
                var renderer = new UniqueValueRenderer { FieldNames = { "status" } };
                foreach (var (status, fill) in fills)
                    renderer.UniqueValues.Add(new UniqueValue(status, status, new SimpleFillSymbol(SimpleFillSymbolStyle.Solid, fill, null), status));
                renderer.DefaultSymbol = renderer.UniqueValues[0].Symbol;
                _fills = NewOverlay();
                _fills.Renderer = renderer;
            }
            _levelSteps = NewOverlay();
            _levelSteps.Renderer = new SimpleRenderer(new SimpleLineSymbol(SimpleLineSymbolStyle.Dot, Color.FromArgb(220, 0, 0, 0), 1.2));
            _memberBorders = NewOverlay();
            _memberBorders.Renderer = new SimpleRenderer(new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, Color.FromArgb(230, 255, 255, 255), 1.2));
            _outlines = NewOverlay();
            _outlines.Renderer = new SimpleRenderer(new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.Black, 1.5));

            // Fills at the bottom, outlines on top
            if (_fills != null)
                view.GraphicsOverlays.Add(_fills);
            view.GraphicsOverlays.Add(_levelSteps);
            view.GraphicsOverlays.Add(_memberBorders);
            view.GraphicsOverlays.Add(_outlines);
        }

        private GraphicsOverlay NewOverlay()
        {
            return _threeD ? new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) }
                           : new GraphicsOverlay();
        }

        // bands: the 3D outlines of the sector (one per FL band, as the prisms), for the heights of the 3D lines
        public void Add(SectorPlan plan, IList<IMergedSector> bands)
        {
            if (plan == null || plan.Outline.Count == 0)
                return;
            if (!_threeD)
            {
                var rings = plan.Outline.Select(Points).ToList();
                if (GeometryEngine.Simplify(new Polygon(rings, SpatialReferences.Wgs84)) is Polygon polygon && !polygon.IsEmpty)
                    _fills.Graphics.Add(Named(new Graphic(polygon), plan.Name, _defaultStatus));
                _outlines.Graphics.Add(Named(new Graphic(new Polyline(plan.Outline.Select(Points), SpatialReferences.Wgs84)), plan.Name, null));
                if (plan.MemberBorders.Count > 0)
                    _memberBorders.Graphics.Add(Named(new Graphic(new Polyline(plan.MemberBorders.Select(Points), SpatialReferences.Wgs84)), plan.Name, null));
                if (plan.LevelSteps.Count > 0)
                    _levelSteps.Graphics.Add(Named(new Graphic(new Polyline(plan.LevelSteps.Select(Points), SpatialReferences.Wgs84)), plan.Name, null));
                return;
            }

            // 3D: each line on the prism it belongs to. The outline is the top ring of every band (the footprint drawn at
            // one height floated above the lower bands and did not match the prisms in a tilted view). Each border between
            // elementary sectors goes at the level where they meet (SectorPlan.MemberBorderLevels): the top of the wall
            // between side-by-side members, or the face between a lower and a higher one
            if (bands == null || bands.Count == 0)
                return;
            foreach (IMergedSector band in bands)
                foreach (var ring in band.OuterRings)
                    _outlines.Graphics.Add(Named(new Graphic(Line3D(ring, TopMetres(band))), plan.Name, null));
            for (int i = 0; i < plan.MemberBorders.Count; i++)
                _memberBorders.Graphics.Add(Named(new Graphic(Line3D(plan.MemberBorders[i], Math.Min(plan.MemberBorderLevels[i], MAX_VISIBLE_FL) * FL_TO_M)),
                                                  plan.Name, null));
        }

        private const double FL_TO_M = 100 * 0.3048;
        private const int MAX_VISIBLE_FL = 600; // same cap as PlotAirspaces3D

        private static double TopMetres(IMergedSector band)
        {
            return Math.Min(band.TopLevel, MAX_VISIBLE_FL) * FL_TO_M;
        }

        // Shows or hides every line (and the 2D fill) of the sector
        public void SetVisible(string name, bool visible)
        {
            foreach (GraphicsOverlay o in Overlays())
                foreach (Graphic g in o.Graphics)
                    if ((string)g.Attributes["name"] == name && g.IsVisible != visible)
                        g.IsVisible = visible;
        }

        public void Remove(string name)
        {
            foreach (GraphicsOverlay o in Overlays())
                foreach (Graphic g in o.Graphics.Where(g => (string)g.Attributes["name"] == name).ToList())
                    o.Graphics.Remove(g);
        }

        // Fill colour of a sector (2D only)
        public void SetStatus(string name, string status)
        {
            if (_fills == null)
                return;
            foreach (Graphic g in _fills.Graphics)
                if ((string)g.Attributes["name"] == name && (string)g.Attributes["status"] != status)
                    g.Attributes["status"] = status;
        }

        // Line options, and the whole plan hidden (density map); the overlays show what all three allow
        private bool _showMembers = true, _showSteps = true, _hidden;
        public bool ShowMemberBorders { get => _showMembers; set { _showMembers = value; ApplyVisibility(); } }
        public bool ShowLevelSteps { get => _showSteps; set { _showSteps = value; ApplyVisibility(); } }
        public bool IsVisible { get => !_hidden; set { _hidden = !value; ApplyVisibility(); } }

        private void ApplyVisibility()
        {
            if (_fills != null)
                _fills.IsVisible = !_hidden;
            _outlines.IsVisible = !_hidden;
            _memberBorders.IsVisible = !_hidden && _showMembers;
            _levelSteps.IsVisible = !_hidden && _showSteps;
        }

        public void Clear()
        {
            foreach (GraphicsOverlay o in Overlays())
                o.Graphics.Clear();
        }

        private IEnumerable<GraphicsOverlay> Overlays()
        {
            return new[] { _fills, _levelSteps, _memberBorders, _outlines }.Where(o => o != null);
        }

        // 3D line just above a prism top at topMetres, densified along geodesics like the prism edges (GlobeHelper.Densify)
        private static Polyline Line3D(List<(double Latitude, double Longitude)> line, double topMetres)
        {
            var flat = new Polyline(Points(line), SpatialReferences.Wgs84);
            var dense = GeometryEngine.DensifyGeodetic(flat, MAX_SEGMENT_KM, LinearUnits.Kilometers, GeodeticCurveType.Geodesic) as Polyline ?? flat;
            double z = topMetres + ABOVE_TOP_M;
            return new Polyline(dense.Parts.Select(part => part.Points.Select(p => new MapPoint(p.X, p.Y, z, SpatialReferences.Wgs84))),
                                SpatialReferences.Wgs84);
        }

        private static IEnumerable<MapPoint> Points(List<(double Latitude, double Longitude)> line)
        {
            return line.Select(v => new MapPoint(v.Longitude, v.Latitude, SpatialReferences.Wgs84));
        }

        private static Graphic Named(Graphic g, string name, string status)
        {
            g.Attributes["name"] = name;
            if (status != null)
                g.Attributes["status"] = status;
            return g;
        }
    }
}
