using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_.ArcGisMethods
{
    /// <summary>Common interface so the 3D and a future 2D plotter can be switched without changing the callers.</summary>
    public interface IAirspacePlotter
    {
        Task PlotAsync(IEnumerable<IMergedSector> sectors, bool zoomTo = true);
        void Remove(string sectorName);
        void Clear();
    }

    /// <summary>Draws fused elementary sectors as extruded prisms in a SceneView.</summary>
    public class PlotAirspaces3D : IAirspacePlotter
    {
        // Flight level (hundreds of feet) -> metres
        private const double FL_TO_M = 100 * 0.3048;

        // FL999 means "no upper limit"; cap it so the prism stays a sensible height (FL600 ~ 18 km)
        private const int MAX_VISIBLE_FL = 600;

        private readonly SceneView _sceneView;
        private readonly GraphicsOverlay _overlay;

        // Default blue. Pass another colour to draw a second, independent group (e.g. collapsed sectors)
        public PlotAirspaces3D(SceneView sceneView) : this(sceneView, Color.FromArgb(120, 0, 120, 255)) { }

        public PlotAirspaces3D(SceneView sceneView, Color fillColor)
        {
            _sceneView = sceneView;

            // Absolute placement: Z is metres above sea level, not above the terrain
            _overlay = new GraphicsOverlay
            {
                SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute)
            };

            var fill = new SimpleFillSymbol(
                SimpleFillSymbolStyle.Solid,
                fillColor,
                new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.White, 1));

            // Extrude upward from the base by the value of the "height" attribute
            var renderer = new SimpleRenderer(fill);
            renderer.SceneProperties.ExtrusionMode = ExtrusionMode.BaseHeight;
            renderer.SceneProperties.ExtrusionExpression = "[height]";
            _overlay.Renderer = renderer;

            _sceneView.GraphicsOverlays.Add(_overlay);
        }

        // Colour per sector: the fill comes from the "status" attribute of each graphic (statusColors: status -> colour,
        // the first one is the colour of a newly plotted sector), outline in outlineColor. SetStatus recolours a sector
        public PlotAirspaces3D(SceneView sceneView, IList<(string Status, Color Fill)> statusColors, Color outlineColor)
        {
            _sceneView = sceneView;
            _overlay = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            _defaultStatus = statusColors[0].Status;

            var renderer = new UniqueValueRenderer { FieldNames = { "status" } };
            foreach (var (status, fill) in statusColors)
            {
                var symbol = new SimpleFillSymbol(SimpleFillSymbolStyle.Solid, fill, new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, outlineColor, 1));
                renderer.UniqueValues.Add(new UniqueValue(status, status, symbol, status));
            }
            renderer.DefaultSymbol = renderer.UniqueValues[0].Symbol;
            renderer.SceneProperties.ExtrusionMode = ExtrusionMode.BaseHeight;
            renderer.SceneProperties.ExtrusionExpression = "[height]";
            _overlay.Renderer = renderer;

            _sceneView.GraphicsOverlays.Add(_overlay);
        }

        private readonly string _defaultStatus; // null: single colour (no status)

        // The graphics of each sector and its status, so removing, recolouring and hiding a sector touch only its own
        // graphics (looking them up by attribute in the whole overlay cost a native call per graphic)
        private readonly Dictionary<string, List<Graphic>> _byName = new Dictionary<string, List<Graphic>>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _statusOf = new Dictionary<string, string>(StringComparer.Ordinal);

        // Prism geometry of each band ring, built once (Simplify + geodetic densify, a few ms each) and reused at every
        // redraw. Key: the ring (same list object while the outlines are the same) and the band's levels. Thread-safe, so
        // Prepare can fill it in the background
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(object Ring, int Bottom, int Top), (Polygon Shape, double HeightM)?> _prisms =
            new System.Collections.Concurrent.ConcurrentDictionary<(object, int, int), (Polygon, double)?>();

        // Forgets the prisms of the previous data set (new outlines)
        public static void ClearCache() => _prisms.Clear();

        // Builds the prism geometry of these bands ahead of time (any thread)
        public static void Prepare(IEnumerable<IMergedSector> sectors)
        {
            foreach (IMergedSector s in sectors)
                foreach (var ring in s.OuterRings)
                    Prism(ring, s.BottomLevel, s.TopLevel);
        }

        private static (Polygon Shape, double HeightM)? Prism(List<(double Latitude, double Longitude)> ring, int bottomFl, int topFl)
        {
            return _prisms.GetOrAdd((ring, bottomFl, topFl), _ =>
            {
                double topM = Math.Min(topFl, MAX_VISIBLE_FL) * FL_TO_M;
                // Ground-level sectors: bottom lifted by the curvature sag so the globe does not hide part of it; top unchanged
                double bottomM = bottomFl == 0 ? CurvatureSag(ring) : bottomFl * FL_TO_M;
                var builder = new PolygonBuilder(SpatialReferences.Wgs84);
                foreach (var v in ring)
                    builder.AddPoint(new MapPoint(v.Longitude, v.Latitude, bottomM, SpatialReferences.Wgs84));
                // Simplify fixes ring orientation and self-intersections
                if (!(GeometryEngine.Simplify(builder.ToGeometry()) is Polygon polygon) || polygon.IsEmpty)
                    return null;
                // Densify so long edges follow the Earth's curvature instead of cutting under the surface
                return (GlobeHelper.Densify(polygon), Math.Max(topM - bottomM, 1));
            });
        }

        // Recolours every polygon of the sector (only with the status constructor)
        public void SetStatus(string sectorName, string status)
        {
            if (!_byName.TryGetValue(sectorName, out List<Graphic> graphics) ||
                (_statusOf.TryGetValue(sectorName, out string current) && current == status))
                return;
            foreach (Graphic g in graphics)
                g.Attributes["status"] = status;
            _statusOf[sectorName] = status;
        }

        public async Task PlotAsync(IEnumerable<IMergedSector> sectors, bool zoomTo = true)
        {
            var list = sectors.ToList();
            var plotted = new List<Geometry>();

            // Replotting a sector replaces it instead of duplicating it
            foreach (string name in list.Select(s => s.Name).Distinct())
                Remove(name);

            var added = new List<Graphic>();
            foreach (var sector in list)
            {
                foreach (var ring in sector.OuterRings)
                {
                    var prism = Prism(ring, sector.BottomLevel, sector.TopLevel);
                    if (prism == null)
                    {
                        Debug.WriteLine($"[PlotAirspaces3D] '{sector.Name}' skipped: invalid polygon.");
                        continue;
                    }
                    var graphic = new Graphic(prism.Value.Shape);
                    graphic.Attributes["height"] = prism.Value.HeightM;
                    graphic.Attributes["name"] = sector.Name;
                    if (_defaultStatus != null)
                        graphic.Attributes["status"] = _defaultStatus;
                    added.Add(graphic);
                    if (!_byName.TryGetValue(sector.Name, out List<Graphic> mine))
                        _byName[sector.Name] = mine = new List<Graphic>();
                    mine.Add(graphic);
                    if (_defaultStatus != null)
                        _statusOf[sector.Name] = _defaultStatus;
                    if (zoomTo)
                        plotted.Add(prism.Value.Shape);
                }
            }
            _overlay.Graphics.AddRange(added);

            if (zoomTo && plotted.Count > 0)
            {
                Envelope extent = GeometryEngine.CombineExtents(plotted);
                if (extent != null)
                    await GlobeHelper.FlyTo(_sceneView, extent);
            }
        }

        private const double EARTH_RADIUS_M = 6371000;

        // The faces of a prism are filled with flat triangles between its edge vertices. On a curved globe the middle of a
        // chord of length d sinks d^2 / 8R below the surface (100 km -> 200 m, 300 km -> 1.8 km), so for a bottom face at
        // 0 m the ground hides part of it (lighter patches). Returns that sag for the ring's diagonal, in metres
        private static double CurvatureSag(List<(double Latitude, double Longitude)> ring)
        {
            double minLat = ring.Min(v => v.Latitude), maxLat = ring.Max(v => v.Latitude);
            double minLon = ring.Min(v => v.Longitude), maxLon = ring.Max(v => v.Longitude);
            double toRad = Math.PI / 180;
            double dy = (maxLat - minLat) * toRad * EARTH_RADIUS_M;
            double dx = (maxLon - minLon) * toRad * EARTH_RADIUS_M * Math.Cos((minLat + maxLat) / 2 * toRad);
            return (dx * dx + dy * dy) / (8 * EARTH_RADIUS_M);
        }

        // Shows or hides every polygon of the sector (selection on the map / sector list)
        public void SetVisible(string sectorName, bool visible)
        {
            if (_byName.TryGetValue(sectorName, out List<Graphic> graphics))
                foreach (Graphic g in graphics)
                    if (g.IsVisible != visible)
                        g.IsVisible = visible;
        }

        public void Remove(string sectorName)
        {
            if (!_byName.TryGetValue(sectorName, out List<Graphic> graphics))
                return;
            foreach (Graphic g in graphics)
                _overlay.Graphics.Remove(g);
            _byName.Remove(sectorName);
            _statusOf.Remove(sectorName);
        }

        public void Clear()
        {
            _overlay.Graphics.Clear();
            _byName.Clear();
            _statusOf.Clear();
        }

        public bool IsVisible
        {
            get => _overlay.IsVisible;
            set => _overlay.IsVisible = value;
        }
    }
}