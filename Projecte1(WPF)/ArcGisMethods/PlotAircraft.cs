using System;
using System.Collections.Generic;
using System.Drawing;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_.ArcGisMethods
{
    /// <summary>
    /// Timeline drawing: one yellow dot per flight in the air (moved on every update) and entry/exit markers that disappear
    /// after a fixed real time, whatever the simulation speed.
    /// </summary>
    public class PlotAircraft
    {
        private const double FL_TO_M = 100 * 0.3048;

        // One overlay per kind, drawn in this order (overlays added later draw on top): entries, exits, aircraft
        private readonly GraphicsOverlay _entries;
        private readonly GraphicsOverlay _exits;
        private readonly GraphicsOverlay _aircraft;
        private readonly Dictionary<long, Graphic> _planes = new Dictionary<long, Graphic>();
        private readonly List<(Graphic Graphic, GraphicsOverlay Overlay, DateTime Expires)> _live = new List<(Graphic, GraphicsOverlay, DateTime)>();

        // Nested sizes, so all three show at one point: green entry 15 > red exit 11 > yellow aircraft 7. Most exits (73 %)
        // are where the plane goes straight into the next sector: the plane stays on the exit point and the next entry is
        // there too. With the exit as small as the plane (7) and drawn under it, the red dot was fully hidden and only the
        // green ring showed. Now a sector change shows green, red and yellow rings, a last exit a red ring around the plane
        private readonly SimpleMarkerSymbol _planeSymbol =
            new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.Yellow, 7) { Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.Black, 1) };
        private readonly SimpleMarkerSymbol _entrySymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.LimeGreen, 15);
        private readonly SimpleMarkerSymbol _exitSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.Red, 11);

        public int AircraftCount { get { return _planes.Count; } }
        public int MarkerCount { get { return _live.Count; } }

        private GeoView _view;

        // view: the 3D scene or the 2D map (the Z of the dots only counts in 3D)
        public PlotAircraft(GeoView view)
        {
            // Absolute placement (Z = FL in metres), same as the sector prisms
            _entries = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            _exits = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            _aircraft = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            _view = view;
            foreach (GraphicsOverlay o in Overlays())
                view.GraphicsOverlays.Add(o);
        }

        private GraphicsOverlay[] Overlays() => new[] { _entries, _exits, _aircraft };

        // Moves the dots and markers to the other view (2D/3D switch), on top of what that view already draws
        public void MoveTo(GeoView view)
        {
            if (view == _view)
                return;
            foreach (GraphicsOverlay o in Overlays())
            {
                _view.GraphicsOverlays.Remove(o);
                view.GraphicsOverlays.Add(o);
            }
            _view = view;
        }

        // Moves the dots of the flights in the air, adds the ones that just departed and removes the ones that landed
        public void UpdateAircraft(List<AircraftPosition> positions)
        {
            var seen = new HashSet<long>();
            foreach (AircraftPosition a in positions)
            {
                long id = a.Flight.FlightIdentifier;
                seen.Add(id);
                var point = new MapPoint(a.Longitude, a.Latitude, a.FL * FL_TO_M, SpatialReferences.Wgs84);
                if (_planes.TryGetValue(id, out Graphic g))
                    g.Geometry = point;
                else
                {
                    g = new Graphic(point, _planeSymbol);
                    g.Attributes["callsign"] = a.Flight.CallSign;
                    _planes[id] = g;
                    _aircraft.Graphics.Add(g);
                }
            }

            var gone = new List<long>();
            foreach (KeyValuePair<long, Graphic> kv in _planes)
                if (!seen.Contains(kv.Key))
                    gone.Add(kv.Key);
            foreach (long id in gone)
            {
                _aircraft.Graphics.Remove(_planes[id]);
                _planes.Remove(id);
            }
        }

        // Entry (green) and exit (red) markers, visible until now + life (real time)
        public void AddMarkers(List<SimEvent> events, TimeSpan life)
        {
            DateTime expires = DateTime.UtcNow + life;
            foreach (SimEvent e in events)
            {
                GraphicsOverlay overlay = e.IsEntry ? _entries : _exits; // exits always on top of entries, whatever the order
                var g = new Graphic(new MapPoint(e.Longitude, e.Latitude, e.FL * FL_TO_M, SpatialReferences.Wgs84),
                                    e.IsEntry ? _entrySymbol : _exitSymbol);
                overlay.Graphics.Add(g);
                _live.Add((g, overlay, expires));
            }
        }

        public void RemoveExpiredMarkers()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].Expires > now)
                    continue;
                _live[i].Overlay.Graphics.Remove(_live[i].Graphic);
                _live.RemoveAt(i);
            }
        }

        public bool IsVisible
        {
            get => _aircraft.IsVisible;
            set { foreach (GraphicsOverlay o in Overlays()) o.IsVisible = value; }
        }

        public void ClearMarkers()
        {
            _entries.Graphics.Clear();
            _exits.Graphics.Clear();
            _live.Clear();
        }

        public void Clear()
        {
            ClearMarkers();
            _aircraft.Graphics.Clear();
            _planes.Clear();
        }
    }
}
