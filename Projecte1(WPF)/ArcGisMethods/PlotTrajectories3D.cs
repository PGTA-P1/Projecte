using System.Collections.Generic;
using System.Drawing;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ArcGisMethods
{
    /// <summary>Draws flights as 3D lines (Z = flight level) and marks points such as sector entries/exits. Used by the crossing test.</summary>
    public class PlotTrajectories3D
    {
        private const double FL_TO_M = 100 * 0.3048;

        private readonly GraphicsOverlay _overlay;

        public PlotTrajectories3D(SceneView sceneView)
        {
            // Absolute placement: Z is metres above sea level, same as the sector prisms
            _overlay = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            sceneView.GraphicsOverlays.Add(_overlay);
        }

        public void PlotFlight(Trajectory t, Color color)
        {
            var builder = new PolylineBuilder(SpatialReferences.Wgs84);
            foreach (TrajectorySegment s in t.Segments)
            {
                if (builder.Parts.Count == 0 || builder.Parts[0].PointCount == 0)
                    builder.AddPoint(new MapPoint(s.LonBegin, s.LatBegin, s.FLBegin * FL_TO_M, SpatialReferences.Wgs84));
                builder.AddPoint(new MapPoint(s.LonEnd, s.LatEnd, s.FLEnd * FL_TO_M, SpatialReferences.Wgs84));
            }
            _overlay.Graphics.Add(new Graphic(builder.ToGeometry(), new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, color, 3)));
        }

        // points: (latitude, longitude, FL). size in pixels: draw a bigger set first and a smaller one on top so points
        // that fall on the same place (e.g. an exit and the next entry) both stay visible
        public void PlotPoints(IEnumerable<(double Latitude, double Longitude, double FL)> points, Color color, double size = 9)
        {
            var symbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, color, size);
            foreach (var p in points)
                _overlay.Graphics.Add(new Graphic(new MapPoint(p.Longitude, p.Latitude, p.FL * FL_TO_M, SpatialReferences.Wgs84), symbol));
        }

        public void Clear() => _overlay.Graphics.Clear();
    }
}
