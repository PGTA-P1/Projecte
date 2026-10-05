using System;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.UI.Controls;

namespace Projecte1_WPF_.ArcGisMethods
{
    /// <summary>
    /// Helpers so that large polygons follow the Earth's curvature and the camera frames them properly.
    /// </summary>
    public static class GlobeHelper
    {
        // Maximum distance between vertices after densifying.
        // Sagitta of a chord is about d^2 / (8R): 25 km -> ~12 m, 100 km -> ~200 m.
        private const double MAX_SEGMENT_KM = 25;

        // true  = edges follow geodesics (great-circle-like arcs)
        // false = edges stay straight in lat/lon (they follow parallels/meridians)
        private const bool USE_GEODESIC = true;

        /// <summary>
        /// Adds vertices along every edge. Without this, a long edge is a straight 3D chord that passes
        /// under the Earth's surface (about 60 km deep for a 1,800 km edge), so the prism is buried.
        /// </summary>
        public static Polygon Densify(Polygon polygon)
        {
            Geometry dense = USE_GEODESIC
                ? GeometryEngine.DensifyGeodetic(polygon, MAX_SEGMENT_KM, LinearUnits.Kilometers, GeodeticCurveType.Geodesic)
                : GeometryEngine.Densify(polygon, MAX_SEGMENT_KM / 111.0); // length in degrees

            return dense as Polygon ?? polygon;
        }

        /// <summary>
        /// Moves the camera so that the whole extent is visible.
        /// Small extents: tilted view from the south. Large extents (&gt; 15 degrees): looking straight down
        /// from high up, so the far side of the sector is not hidden below the horizon.
        /// </summary>
        public static async Task FlyTo(SceneView sceneView, Envelope extent)
        {
            double sizeDeg = Math.Max(extent.Width, extent.Height);
            bool large = sizeDeg > 15;

            MapPoint c = extent.GetCenter();
            double latOffset = large ? 0 : Math.Max(extent.Height * 1.5, 0.5);   // degrees south of the centre
            double pitch = large ? 0 : 60;                                       // 0 = straight down
            double altitude = Math.Min(Math.Max(sizeDeg * 111_000 * 2, 200_000), 30_000_000); // metres

            // Latitude must stay inside [-90, 90]
            double camLat = Math.Max(-85, Math.Min(85, c.Y - latOffset));

            await sceneView.SetViewpointCameraAsync(new Camera(camLat, c.X, altitude, 0, pitch, 0));
        }
    }
}