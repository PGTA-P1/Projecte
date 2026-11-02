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

        private readonly GraphicsOverlay _markers;
        private readonly GraphicsOverlay _aircraft;
        private readonly Dictionary<long, Graphic> _planes = new Dictionary<long, Graphic>();
        private readonly List<(Graphic Graphic, DateTime Expires)> _live = new List<(Graphic Graphic, DateTime Expires)>();

        private readonly SimpleMarkerSymbol _planeSymbol =
            new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.Yellow, 7) { Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, Color.Black, 1) };
        // Entries big, exits small on top: an exit and the next entry on the same boundary show as a red dot in a green ring
        private readonly SimpleMarkerSymbol _entrySymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.LimeGreen, 13);
        private readonly SimpleMarkerSymbol _exitSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.Red, 7);

        public int AircraftCount { get { return _planes.Count; } }
        public int MarkerCount { get { return _live.Count; } }

        public PlotAircraft(SceneView sceneView)
        {
            // Absolute placement (Z = FL in metres), same as the sector prisms. Markers first so the aircraft draw on top
            _markers = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            _aircraft = new GraphicsOverlay { SceneProperties = new LayerSceneProperties(SurfacePlacement.Absolute) };
            sceneView.GraphicsOverlays.Add(_markers);
            sceneView.GraphicsOverlays.Add(_aircraft);
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
            foreach (bool entries in new[] { true, false }) // entries first so exits are drawn on top
            {
                foreach (SimEvent e in events)
                {
                    if (e.IsEntry != entries)
                        continue;
                    var g = new Graphic(new MapPoint(e.Longitude, e.Latitude, e.FL * FL_TO_M, SpatialReferences.Wgs84),
                                        e.IsEntry ? _entrySymbol : _exitSymbol);
                    _markers.Graphics.Add(g);
                    _live.Add((g, expires));
                }
            }
        }

        public void RemoveExpiredMarkers()
        {
            DateTime now = DateTime.UtcNow;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i].Expires > now)
                    continue;
                _markers.Graphics.Remove(_live[i].Graphic);
                _live.RemoveAt(i);
            }
        }

        public bool IsVisible
        {
            get => _aircraft.IsVisible;
            set { _aircraft.IsVisible = value; _markers.IsVisible = value; }
        }

        public void ClearMarkers()
        {
            _markers.Graphics.Clear();
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
