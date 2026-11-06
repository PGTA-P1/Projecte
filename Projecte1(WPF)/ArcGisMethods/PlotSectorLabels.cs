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

        public void Add(string name, IEnumerable<IMergedSector> bands)
        {
            List<IMergedSector> list = bands.ToList();
            var ring = list.SelectMany(b => b.OuterRings).OrderByDescending(BoxArea).FirstOrDefault();
            if (ring == null || ring.Count < 3)
                return;

            var builder = new PolygonBuilder(SpatialReferences.Wgs84);
            foreach (var v in ring)
                builder.AddPoint(v.Longitude, v.Latitude);
            if (!(GeometryEngine.Simplify(builder.ToGeometry()) is Polygon polygon) || polygon.IsEmpty)
                return;
            MapPoint at = GeometryEngine.LabelPoint(polygon);
            double topM = Math.Min(list.Max(b => b.TopLevel), MAX_VISIBLE_FL) * FL_TO_M;

            // Sectors stacked over the same ground (e.g. a lower and an upper sector) get the same label point: each label
            // that would cover one already there goes one line lower
            int covered = _overlay.Graphics.Count(g => g.Geometry is MapPoint p &&
                                                       Math.Abs(p.Y - at.Y) < OVERLAP_LAT && Math.Abs(p.X - at.X) < OVERLAP_LON);
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
        }

        public void SetVisible(string name, bool visible)
        {
            foreach (Graphic g in _overlay.Graphics)
                if ((string)g.Attributes["name"] == name && g.IsVisible != visible)
                    g.IsVisible = visible;
        }

        public void Remove(string name)
        {
            foreach (Graphic g in _overlay.Graphics.Where(g => (string)g.Attributes["name"] == name).ToList())
                _overlay.Graphics.Remove(g);
        }

        public void Clear() => _overlay.Graphics.Clear();

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
