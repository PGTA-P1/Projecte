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
        public SectorEntry Source;   // the entry this dot belongs to (its entry dot and exit dot share it)
    }

    public struct AircraftPosition
    {
        public Trajectory Flight;
        public double Latitude, Longitude, FL;
        public List<string> Sectors;   // open sectors the flight is inside at that time (selection on the map)
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
            public List<Interval> Inside = new List<Interval>(); // its stays in open sectors, sorted and joined (Interval.Merge)
            public List<(string Sector, DateTime Entry, DateTime Exit)> Stays = new List<(string, DateTime, DateTime)>(); // per sector
        }

        private readonly List<VisibleFlight> _flights = new List<VisibleFlight>(); // sorted by From
        private readonly List<SimEvent> _events = new List<SimEvent>();           // sorted by time

        public DateTime Start { get; }
        public DateTime End { get; }
        public int FlightCount { get { return _flights.Count; } }
        public int EventCount { get { return _events.Count; } }

        // entries: only Level Open is used (the entries counted per open sector). Each one becomes a green entry dot and
        // a red exit dot; the results table (EntryTable) is built from these dots when the user exports it
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

                // Both dots must be placeable; otherwise the entry is neither plotted nor listed
                SimEvent? entry = MakeEvent(t, e, e.Entry, true);
                SimEvent? exit = MakeEvent(t, e, e.Exit, false);
                if (entry == null || exit == null)
                    continue;
                _events.Add(entry.Value);
                _events.Add(exit.Value);

                if (!windows.TryGetValue(e.FlightId, out VisibleFlight w))
                    windows[e.FlightId] = w = new VisibleFlight { Flight = t, From = e.Entry, To = e.Exit };
                if (e.Entry < w.From) w.From = e.Entry;
                if (e.Exit > w.To) w.To = e.Exit;
                w.Inside.Add(new Interval(e.Entry, e.Exit));
                w.Stays.Add((e.Sector, e.Entry, e.Exit));
            }
            if (windows.Count == 0)
                throw new InvalidOperationException("No open-sector entries to simulate (run ComputeEntries first)");

            foreach (VisibleFlight w in windows.Values)
                w.Inside = Interval.Merge(w.Inside, Interval.TOUCH);
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

        // Entry or exit dot at the flight's position at that time; null if the flight is not in the air then
        private static SimEvent? MakeEvent(Trajectory t, SectorEntry e, DateTime time, bool isEntry)
        {
            if (!t.TryGetPosition(time, out double lat, out double lon, out double fl))
                return null;
            return new SimEvent
            {
                Time = time, IsEntry = isEntry, FlightId = e.FlightId, Sector = e.Sector,
                Latitude = lat, Longitude = lon, FL = fl, Source = e
            };
        }

        // Flights inside an open sector at time t, with their positions: only during their stays, so a plane between two
        // open sectors (e.g. crossing Portugal or a gap between ACCs) is not drawn outside the sectors. Sorted by first
        // entry, so the loop stops at the first flight that has not entered yet
        public List<AircraftPosition> PositionsAt(DateTime t)
        {
            var result = new List<AircraftPosition>();
            foreach (VisibleFlight f in _flights)
            {
                if (f.From > t)
                    break;
                if (t <= f.To && IsInside(f, t) && f.Flight.TryGetPosition(t, out double lat, out double lon, out double fl))
                {
                    var sectors = new List<string>(2);
                    foreach (var s in f.Stays)
                        if (s.Entry <= t && t <= s.Exit)
                            sectors.Add(s.Sector);
                    result.Add(new AircraftPosition { Flight = f.Flight, Latitude = lat, Longitude = lon, FL = fl, Sectors = sectors });
                }
            }
            return result;
        }

        private static bool IsInside(VisibleFlight f, DateTime t)
        {
            foreach (Interval stay in f.Inside)
            {
                if (stay.Start > t)
                    return false;
                if (t <= stay.End)
                    return true;
            }
            return false;
        }

        // Events with from < Time <= to, in time order
        public List<SimEvent> EventsBetween(DateTime from, DateTime to)
        {
            var result = new List<SimEvent>();
            for (int i = FirstEventAfter(from); i < _events.Count && _events[i].Time <= to; i++)
                result.Add(_events[i]);
            return result;
        }

        // Time of the next entry into a sector after t (when the next aircraft appears), null if there is none
        public DateTime? NextEntryAfter(DateTime t)
        {
            for (int i = FirstEventAfter(t); i < _events.Count; i++)
                if (_events[i].IsEntry)
                    return _events[i].Time;
            return null;
        }

        // Index of the first event with Time > t (binary search)
        private int FirstEventAfter(DateTime t)
        {
            int lo = 0, hi = _events.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_events[mid].Time <= t) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
