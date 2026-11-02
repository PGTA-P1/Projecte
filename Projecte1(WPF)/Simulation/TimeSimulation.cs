using System;
using System.Collections.Generic;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_.Simulation
{
    // Entry into or exit from an open sector, with where it happened (timeline markers)
    public struct SimEvent
    {
        public DateTime Time;
        public bool IsEntry;
        public long FlightId;
        public string Sector;
        public double Latitude, Longitude, FL;
    }

    public struct AircraftPosition
    {
        public Trajectory Flight;
        public double Latitude, Longitude, FL;
    }

    // Time simulation over the relevant trajectories, without any UI: which flights are in the airspace of the ACCs at a
    // time and where, and which open-sector entries/exits happen between two instants. The timeline in MainWindow drives it.
    // A flight is visible only from its first entry into an open sector to its last exit, not for its whole trip.
    public class TimeSimulation
    {
        private class VisibleFlight
        {
            public Trajectory Flight;
            public DateTime From;   // first entry into an open sector
            public DateTime To;     // last exit
        }

        private readonly List<VisibleFlight> _flights = new List<VisibleFlight>(); // sorted by From
        private readonly List<SimEvent> _events = new List<SimEvent>();           // sorted by time

        public DateTime Start { get; }
        public DateTime End { get; }
        public int FlightCount { get { return _flights.Count; } }
        public int EventCount { get { return _events.Count; } }

        // entries: only Level Open is used (the entries counted per open sector)
        public TimeSimulation(IEnumerable<Trajectory> trajectories, IEnumerable<SectorEntry> entries)
        {
            var byId = new Dictionary<long, Trajectory>();
            foreach (Trajectory t in trajectories)
                if (t.Segments.Count > 0)
                    byId[t.FlightIdentifier] = t;

            var windows = new Dictionary<long, VisibleFlight>();
            foreach (SectorEntry e in entries)
            {
                if (e.Level != EntryLevel.Open || !byId.TryGetValue(e.FlightId, out Trajectory t))
                    continue;
                if (!windows.TryGetValue(e.FlightId, out VisibleFlight w))
                    windows[e.FlightId] = w = new VisibleFlight { Flight = t, From = e.Entry, To = e.Exit };
                if (e.Entry < w.From) w.From = e.Entry;
                if (e.Exit > w.To) w.To = e.Exit;
                AddEvent(t, e, e.Entry, true);
                AddEvent(t, e, e.Exit, false);
            }
            if (windows.Count == 0)
                throw new InvalidOperationException("No open-sector entries to simulate (run ComputeEntries first)");

            _flights.AddRange(windows.Values);
            _flights.Sort((a, b) => a.From.CompareTo(b.From));
            _events.Sort((a, b) => a.Time.CompareTo(b.Time));

            // Starts when the first plane that crosses the ACCs takes off (its first entry, if it takes off inside)
            VisibleFlight first = _flights[0];
            Start = first.Flight.Start < first.From ? first.Flight.Start : first.From;
            End = Start;
            foreach (VisibleFlight f in _flights)
                if (f.To > End) End = f.To;
        }

        private void AddEvent(Trajectory t, SectorEntry e, DateTime time, bool isEntry)
        {
            if (!t.TryGetPosition(time, out double lat, out double lon, out double fl))
                return;
            _events.Add(new SimEvent
            {
                Time = time, IsEntry = isEntry, FlightId = e.FlightId, Sector = e.Sector,
                Latitude = lat, Longitude = lon, FL = fl
            });
        }

        // Flights inside the airspace of the ACCs at time t (between their first entry and last exit) with their positions.
        // Sorted by first entry, so the loop stops at the first flight that has not entered yet
        public List<AircraftPosition> PositionsAt(DateTime t)
        {
            var result = new List<AircraftPosition>();
            foreach (VisibleFlight f in _flights)
            {
                if (f.From > t)
                    break;
                if (t <= f.To && f.Flight.TryGetPosition(t, out double lat, out double lon, out double fl))
                    result.Add(new AircraftPosition { Flight = f.Flight, Latitude = lat, Longitude = lon, FL = fl });
            }
            return result;
        }

        // Events with from < Time <= to, in time order
        public List<SimEvent> EventsBetween(DateTime from, DateTime to)
        {
            var result = new List<SimEvent>();

            // First event after `from` (binary search)
            int lo = 0, hi = _events.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_events[mid].Time <= from) lo = mid + 1;
                else hi = mid;
            }

            for (int i = lo; i < _events.Count && _events[i].Time <= to; i++)
                result.Add(_events[i]);
            return result;
        }
    }
}
