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
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ArcGisMethods
{
    public static class PlotAirspaces
    {
        private const string OverlayId = "airspaces";

        // Flight level (hundreds of feet) -> metres.
        // Not used while the heights are fixed; restore it when real levels are available.
        // private const double FL_TO_M = 100 * 0.3048;

        public static async Task PlotAirblock(SceneView sceneView, Airblock airblock, bool zoomTo = true)
        {
            if (airblock?.Vertices == null || airblock.Vertices.Count < 3)
            {
                Debug.WriteLine($"[PlotAirspaces] '{airblock?.Name}' skipped: fewer than 3 vertices.");
                return;
            }

            // Temporary: no Z defined yet, so every sector goes from the ground to 10 km.
            // When real levels are available, use:
            //   double bottomM = airblock.BottomLevel * FL_TO_M;
            //   double topM = airblock.TopLevel * FL_TO_M;
            double bottomM = 0;
            double topM = 10_000;
            double heightM = Math.Max(topM - bottomM, 1);

            // Vertices are (Latitude, Longitude) in decimal degrees (WGS84).
            // Drop the closing vertex if it repeats the first one.
            var verts = new List<(double Latitude, double Longitude)>(airblock.Vertices);
            if (verts.Count > 3 && verts[0].Equals(verts[verts.Count - 1]))
                verts.RemoveAt(verts.Count - 1);

            // Sanity check: everything must be valid decimal degrees.
            if (verts.Any(v => double.IsNaN(v.Latitude) || double.IsNaN(v.Longitude)
                            || Math.Abs(v.Latitude) > 90 || Math.Abs(v.Longitude) > 180))
            {
                Debug.WriteLine($"[PlotAirspaces] '{airblock.Name}' skipped: coordinates out of range. " +
                                $"First vertex (lat,lon) = ({verts[0].Latitude}, {verts[0].Longitude}). " +
                                "They are probably not decimal degrees, or lat/lon are swapped in the reader.");
                return;
            }

            // MapPoint takes x = longitude, y = latitude
            var builder = new PolygonBuilder(SpatialReferences.Wgs84);
            foreach (var v in verts)
                builder.AddPoint(new MapPoint(v.Longitude, v.Latitude, bottomM, SpatialReferences.Wgs84));

            // Simplify fixes ring orientation and self-intersections
            Geometry simplified = GeometryEngine.Simplify(builder.ToGeometry());
            if (!(simplified is Polygon polygon) || polygon.IsEmpty)
            {
                Debug.WriteLine($"[PlotAirspaces] '{airblock.Name}' skipped: invalid or empty polygon.");
                return;
            }

            Debug.WriteLine(
                $"[PlotAirspaces] '{airblock.Name}' n={verts.Count} " +
                $"first(lat,lon)=({verts[0].Latitude}, {verts[0].Longitude}) " +
                $"extent X[{polygon.Extent.XMin:F3},{polygon.Extent.XMax:F3}] " +
                $"Y[{polygon.Extent.YMin:F3},{polygon.Extent.YMax:F3}]");

            // One shared overlay for all airblocks
            var overlay = sceneView.GraphicsOverlays.FirstOrDefault(o => o.Id == OverlayId);
            if (overlay == null)
            {
                overlay = new GraphicsOverlay
                {
                    Id = OverlayId,
                    // Absolute placement: Z is metres above sea level, not above the terrain
                    SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute)
                };

                var fill = new SimpleFillSymbol(
                    SimpleFillSymbolStyle.Solid,
                    Color.FromArgb(120, 0, 120, 255),
                    new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.White, 1));

                // Extrude upward from the base by the value of the "height" attribute
                var renderer = new SimpleRenderer(fill);
                renderer.SceneProperties.ExtrusionMode = ExtrusionMode.BaseHeight;
                renderer.SceneProperties.ExtrusionExpression = "[height]";
                overlay.Renderer = renderer;

                sceneView.GraphicsOverlays.Add(overlay);
            }

            var graphic = new Graphic(polygon);
            graphic.Attributes["height"] = heightM;
            graphic.Attributes["name"] = airblock.Name;
            overlay.Graphics.Add(graphic);

            if (zoomTo)
            {
                // Camera from the south with a tilt, scaled to the size of the sector
                MapPoint c = polygon.Extent.GetCenter();
                double latOffset = Math.Max(polygon.Extent.Height * 1.5, 0.5);            // degrees
                double altitude = Math.Max(polygon.Extent.Height * 111_000 * 2, 200_000); // metres
                await sceneView.SetViewpointCameraAsync(new Camera(c.Y - latOffset, c.X, altitude, 0, 60, 0));
            }
        }
    }
}