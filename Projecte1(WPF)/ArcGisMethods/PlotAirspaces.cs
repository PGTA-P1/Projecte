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

        // Recolours every polygon of the sector (only with the status constructor)
        public void SetStatus(string sectorName, string status)
        {
            foreach (var g in _overlay.Graphics)
                if ((string)g.Attributes["name"] == sectorName && (string)g.Attributes["status"] != status)
                    g.Attributes["status"] = status;
        }

        public async Task PlotAsync(IEnumerable<IMergedSector> sectors, bool zoomTo = true)
        {
            var list = sectors.ToList();
            var plotted = new List<Geometry>();

            // Replotting a sector replaces it instead of duplicating it
            foreach (string name in list.Select(s => s.Name).Distinct())
                Remove(name);

            foreach (var sector in list)
            {
                double topM = Math.Min(sector.TopLevel, MAX_VISIBLE_FL) * FL_TO_M;

                foreach (var ring in sector.OuterRings)
                {
                    // Ground-level sectors: bottom lifted by the curvature sag so the globe does not hide part of it; top unchanged
                    double bottomM = sector.BottomLevel == 0 ? CurvatureSag(ring) : sector.BottomLevel * FL_TO_M;
                    double heightM = Math.Max(topM - bottomM, 1);

                    var builder = new PolygonBuilder(SpatialReferences.Wgs84);
                    foreach (var v in ring)
                        builder.AddPoint(new MapPoint(v.Longitude, v.Latitude, bottomM, SpatialReferences.Wgs84));

                    // Simplify fixes ring orientation and self-intersections
                    if (!(GeometryEngine.Simplify(builder.ToGeometry()) is Polygon polygon) || polygon.IsEmpty)
                    {
                        Debug.WriteLine($"[PlotAirspaces3D] '{sector.Name}' skipped: invalid polygon.");
                        continue;
                    }

                    // Densify so long edges follow the Earth's curvature instead of cutting under the surface
                    var graphic = new Graphic(GlobeHelper.Densify(polygon));
                    graphic.Attributes["height"] = heightM;
                    graphic.Attributes["name"] = sector.Name;
                    if (_defaultStatus != null)
                        graphic.Attributes["status"] = _defaultStatus;
                    _overlay.Graphics.Add(graphic);
                    plotted.Add(polygon);
                }
            }

            Debug.WriteLine($"[PlotAirspaces3D] {plotted.Count} polygons added, {_overlay.Graphics.Count} on screen.");

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
            foreach (var g in _overlay.Graphics)
                if ((string)g.Attributes["name"] == sectorName && g.IsVisible != visible)
                    g.IsVisible = visible;
        }

        public void Remove(string sectorName)
        {
            foreach (var g in _overlay.Graphics.Where(g => (string)g.Attributes["name"] == sectorName).ToList())
                _overlay.Graphics.Remove(g);
        }

        public void Clear() => _overlay.Graphics.Clear();

        public bool IsVisible
        {
            get => _overlay.IsVisible;
            set => _overlay.IsVisible = value;
        }
    }
}