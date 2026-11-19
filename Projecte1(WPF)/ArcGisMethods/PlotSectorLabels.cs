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
    // Name label of each sector drawn by PlotOpenSectors: one per sector of the open configurations, so a collapsed sector
    // gets its own name, never those of the elementary sectors inside it. The label sits inside the largest outline of the
    // sector (GeometryEngine.LabelPoint, inside even for concave shapes), at the height of its top so it shows above
    // the prism; text keeps its screen size whatever the zoom
    public class PlotSectorLabels
    {
        private const double FL_TO_M = 100 * 0.3048;
        private const int MAX_VISIBLE_FL = 600; // same cap as PlotAirspaces3D

        // Two label points closer than this (degrees) overlap when the whole area of the ACCs is in view (a 7-letter name is
        // about 0.9 deg wide and 0.15 deg high there); the later label is moved down LINE_PX per label it would cover
        private const double OVERLAP_LAT = 0.17, OVERLAP_LON = 1.3;
        private const double LINE_PX = 14;

        private readonly GraphicsOverlay _overlay;
        private GeoView _view;

        // view: the 3D scene or the 2D map (the Z of the labels only counts in 3D)
        public PlotSectorLabels(GeoView view)
        {
            _overlay = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            _view = view;
            view.GraphicsOverlays.Add(_overlay);
        }

        // Moves the labels to the other view (2D/3D switch), on top of what that view already draws
        public void MoveTo(GeoView view)
        {
            if (view == _view)
                return;
            _view.GraphicsOverlays.Remove(_overlay);
            view.GraphicsOverlays.Add(_overlay);
            _view = view;
        }

        // Label point of a ring (Simplify + LabelPoint), computed once (thread-safe, so Prepare can fill it in the background)
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<object, MapPoint> _points =
            new System.Collections.Concurrent.ConcurrentDictionary<object, MapPoint>();

        public static void ClearCache() => _points.Clear();

        // The ring the label goes in: the largest outline of the sector's bands
        private static List<(double Latitude, double Longitude)> LabelRing(IEnumerable<IMergedSector> bands) =>
            bands.SelectMany(b => b.OuterRings).OrderByDescending(BoxArea).FirstOrDefault();

        private static MapPoint PointOf(List<(double Latitude, double Longitude)> ring) => _points.GetOrAdd(ring, _ =>
        {
            var builder = new PolygonBuilder(SpatialReferences.Wgs84);
            foreach (var v in ring)
                builder.AddPoint(v.Longitude, v.Latitude);
            return GeometryEngine.Simplify(builder.ToGeometry()) is Polygon polygon && !polygon.IsEmpty ? GeometryEngine.LabelPoint(polygon) : null;
        });

        // Computes the label point of these bands ahead of time (any thread)
        public static void Prepare(IEnumerable<IMergedSector> bands)
        {
            var ring = LabelRing(bands);
            if (ring != null && ring.Count >= 3)
                PointOf(ring);
        }

        // Labels drawn now, by name, with their point (the overlap test reads these instead of the overlay's graphics)
        private readonly Dictionary<string, (Graphic Graphic, MapPoint At)> _byName = new Dictionary<string, (Graphic, MapPoint)>(StringComparer.Ordinal);

        public void Add(string name, IEnumerable<IMergedSector> bands)
        {
            List<IMergedSector> list = bands.ToList();
            var ring = LabelRing(list);
            if (ring == null || ring.Count < 3)
                return;
            MapPoint at = PointOf(ring);
            if (at == null)
                return;
            double topM = Math.Min(list.Max(b => b.TopLevel), MAX_VISIBLE_FL) * FL_TO_M;
            Remove(name);

            // Sectors stacked over the same ground (e.g. a lower and an upper sector) get the same label point: each label
            // that would cover one already there goes one line lower
            int covered = _byName.Values.Count(v => Math.Abs(v.At.Y - at.Y) < OVERLAP_LAT && Math.Abs(v.At.X - at.X) < OVERLAP_LON);
            var text = new TextSymbol(name, Color.White, 11,
                                      Esri.ArcGISRuntime.Symbology.HorizontalAlignment.Center,
                                      Esri.ArcGISRuntime.Symbology.VerticalAlignment.Middle)
            {
                HaloColor = Color.Black,
                HaloWidth = 1.5,
                OffsetY = -LINE_PX * covered
            };
            var graphic = new Graphic(new MapPoint(at.X, at.Y, topM, SpatialReferences.Wgs84), text);
            graphic.Attributes["name"] = name;
            _overlay.Graphics.Add(graphic);
            _byName[name] = (graphic, at);
        }

        public void SetVisible(string name, bool visible)
        {
            if (_byName.TryGetValue(name, out var label) && label.Graphic.IsVisible != visible)
                label.Graphic.IsVisible = visible;
        }

        public void Remove(string name)
        {
            if (!_byName.TryGetValue(name, out var label))
                return;
            _overlay.Graphics.Remove(label.Graphic);
            _byName.Remove(name);
        }

        public void Clear()
        {
            _overlay.Graphics.Clear();
            _byName.Clear();
        }

        public bool IsVisible
        {
            get => _overlay.IsVisible;
            set => _overlay.IsVisible = value;
        }

        private static double BoxArea(List<(double Latitude, double Longitude)> ring)
        {
            return (ring.Max(v => v.Latitude) - ring.Min(v => v.Latitude)) * (ring.Max(v => v.Longitude) - ring.Min(v => v.Longitude));
        }
    }
}
