using System;
using System.Drawing;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.Simulation;
using Color = System.Drawing.Color;

namespace Projecte1_WPF_.ArcGisMethods
{
    // TRAFFIC DENSITY layer (TrafficDensity) in both views: ONE image per view (ArcGIS ImageOverlay), a smooth surface
    // coloured from blue (little) to red (most) that fades out where there is almost no traffic. Image overlays draw under
    // every graphics overlay, so aircraft and markers stay on top, and cost one texture: no lag however much traffic.
    // (It replaced 0.05° squares in 2D and 17,000 extruded 0.1° x FL30 boxes in 3D, which looked blocky and slowed the
    // globe down.) The 2D image is made in Web Mercator rows, the 3D one in latitude rows, each matching its view
    public class PlotDensity
    {
        // Colour ramp, from the least to the most traffic (also the legend)
        public static readonly Color[] RAMP =
        {
            Color.FromArgb(43, 131, 186),
            Color.FromArgb(102, 194, 165),
            Color.FromArgb(171, 221, 164),
            Color.FromArgb(254, 224, 139),
            Color.FromArgb(253, 174, 97),
            Color.FromArgb(215, 25, 28),
        };
        private const byte ALPHA_LOW = 90, ALPHA_HIGH = 235;
        private const int UPSAMPLE = 2;   // image pixels per grid cell, so the bilinear surface stays smooth when zoomed in

        private readonly ImageOverlay _scene = new ImageOverlay { IsVisible = false };
        private readonly ImageOverlay _map = new ImageOverlay { IsVisible = false };

        public PlotDensity(SceneView sceneView, MapView mapView)
        {
            sceneView.ImageOverlays.Add(_scene);
            mapView.ImageOverlays.Add(_map);
        }

        public void Show(ImageFrame sceneFrame, ImageFrame mapFrame)
        {
            _scene.ImageFrame = sceneFrame;
            _map.ImageFrame = mapFrame;
            _scene.IsVisible = _map.IsVisible = true;
        }

        public void Clear()
        {
            _scene.IsVisible = _map.IsVisible = false;
        }

        // The image of the density for a view in spatial reference sr (geographic: rows equal in latitude; otherwise Web
        // Mercator: rows equal in Mercator y), as PNG bytes, and its extent. Thread-safe: run it in the background
        public static ImageFrame Render(TrafficDensity d, SpatialReference sr)
        {
            bool geographic = sr == null || sr.IsGeographic;
            int width = d.Cols * UPSAMPLE, height = d.Rows * UPSAMPLE;
            double yMax = MercatorY(d.MaxLat), yMin = MercatorY(d.MinLat);
            var pixels = new byte[width * height * 4];
            for (int py = 0; py < height; py++)
            {
                double f = (py + 0.5) / height;  // 0 at the top (north)
                double lat = geographic ? d.MaxLat - f * (d.MaxLat - d.MinLat) : Latitude(yMax - f * (yMax - yMin));
                for (int px = 0; px < width; px++)
                {
                    double lon = d.MinLon + (px + 0.5) / width * (d.MaxLon - d.MinLon);
                    Color c = ColorOf(d, d.ValueAt(lat, lon));
                    int i = (py * width + px) * 4;
                    pixels[i] = c.B; pixels[i + 1] = c.G; pixels[i + 2] = c.R; pixels[i + 3] = c.A;
                }
            }
            BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            byte[] png;
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                png = stream.ToArray();
            }

            var extent = new Envelope(d.MinLon, d.MinLat, d.MaxLon, d.MaxLat, SpatialReferences.Wgs84);
            if (!geographic)
                extent = (Envelope)GeometryEngine.Project(extent, sr);
            return new ImageFrame(new RuntimeImage(png), extent);
        }

        // Colour of a value: transparent below the first knot (fading in just under it), then the ramp, more opaque the busier
        private static Color ColorOf(TrafficDensity d, double value)
        {
            double pos = d.RampPosition(value);
            if (pos < 0)
            {
                double k0 = d.Knots[0];
                double fade = k0 <= 0 ? 0 : (value - k0 * 0.3) / (k0 * 0.7);  // 0 at 30 % of the first knot, 1 at it
                return fade <= 0 ? Color.Transparent : Color.FromArgb((int)(fade * ALPHA_LOW), RAMP[0]);
            }
            double x = pos * (RAMP.Length - 1);
            int i = Math.Min(RAMP.Length - 2, (int)x);
            double t = x - i;
            Color a = RAMP[i], b = RAMP[i + 1];
            int alpha = (int)(ALPHA_LOW + pos * (ALPHA_HIGH - ALPHA_LOW));
            return Color.FromArgb(alpha, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        private static double MercatorY(double lat) => Math.Log(Math.Tan(Math.PI / 4 + lat * Math.PI / 360));
        private static double Latitude(double y) => (2 * Math.Atan(Math.Exp(y)) - Math.PI / 2) * 180 / Math.PI;
    }
}
