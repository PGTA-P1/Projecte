using System;
using System.Collections.Generic;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_.Simulation
{
    // Project parameters of the demand (minutes)
    public class DemandParameters
    {
        // Length of the integration window in which the entries are counted, half-open [start, start + window)
        public double WindowMinutes { get; set; } = 20;

        // A new window starts every sliding step (windows overlap when the step is shorter than the window)
        public double StepMinutes { get; set; } = 5;
    }

    // Demand of a sector against its capacity in the window shown: green below NEAR_LIMIT of the capacity, yellow from
    // NEAR_LIMIT up to the capacity (equal still yellow), red above it (overload: demand > capacity)
    public enum CapacityStatus { Green, Yellow, Red }

    // DEMAND (slides "Demand"): entries into each open sector counted in an integration window [start, start + window),
    // recomputed every sliding step, and compared with the capacity in that window. Capacity file values are per hour,
    // so the capacity of a window = hourly capacity x window / 60 min. Capacity 999 or no capacity = no limit (green).
    // Windows start on the clock (00:00 UTC + k step). At time t the window shown is the earliest one that still contains
    // t, but never one starting before the simulation: first [s0, s0 + window) with s0 = Start rounded down to the step,
    // then, once it ends, each later window in turn, e.g. window 20, step 5: 10:00-10:20, at 10:20 10:05-10:25, at 10:25
    // 10:10-10:30
    public class SectorDemand
    {
        // Demand / capacity from which a sector is near its limit (yellow)
        public const double NEAR_LIMIT = 0.8;

        // Hourly capacity at or above this value means no limit (the file uses 999)
        private const double UNLIMITED = 999;

        private readonly Dictionary<string, List<DateTime>> _entryTimes =
            new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase); // sorted
        private readonly Dictionary<string, double> _hourly = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly TimeSpan _window, _step;
        private readonly DateTime _firstStart;

        public double WindowMinutes { get { return _window.TotalMinutes; } }
        public TimeSpan Window { get { return _window; } }
        public TimeSpan Step { get { return _step; } }

        // Start of the first window: the simulation start rounded down to the step. Later windows start every Step
        public DateTime FirstStart { get { return _firstStart; } }

        // entries: only Level Open counts (entries into the sectors open per the opening scheme, also those opened while
        // the flight was inside). start: start of the simulation (first window)
        public SectorDemand(IEnumerable<SectorEntry> entries, IEnumerable<CapacityRecord> capacities, DemandParameters p, DateTime start)
        {
            if (p.WindowMinutes <= 0 || p.StepMinutes <= 0 || p.StepMinutes > p.WindowMinutes)
                throw new ArgumentException("The window and the step must be > 0, and the step not longer than the window");
            _window = TimeSpan.FromMinutes(p.WindowMinutes);
            _step = TimeSpan.FromMinutes(p.StepMinutes);
            _firstStart = start.Date + TimeSpan.FromTicks((start - start.Date).Ticks / _step.Ticks * _step.Ticks);

            foreach (SectorEntry e in entries)
            {
                if (e.Level != EntryLevel.Open)
                    continue;
                if (!_entryTimes.TryGetValue(e.Sector, out List<DateTime> list))
                    _entryTimes[e.Sector] = list = new List<DateTime>();
                list.Add(e.Entry);
            }
            foreach (List<DateTime> list in _entryTimes.Values)
                list.Sort();

            foreach (CapacityRecord c in capacities)
                _hourly[c.SectorName] = c.HourlyCapacity;
        }

        // Window shown at time t: the earliest window [start, start + window) that contains t, the first one at most
        public (DateTime Start, DateTime End) WindowAt(DateTime t)
        {
            DateTime start = _firstStart;
            if (t >= _firstStart + _window)
            {
                // Earliest grid start s with t < s + window, i.e. s > t - window
                long k = (t - _window - _firstStart).Ticks / _step.Ticks + 1;
                start = _firstStart + TimeSpan.FromTicks(k * _step.Ticks);
            }
            return (start, start + _window);
        }

        // Entries into the sector with start <= entry time < end
        public int Demand(string sector, DateTime start, DateTime end)
        {
            if (!_entryTimes.TryGetValue(sector, out List<DateTime> times))
                return 0;
            return FirstAtOrAfter(times, end) - FirstAtOrAfter(times, start);
        }

        // Capacity in one window (hourly x window / 60), null = no limit (999 or not in the capacity file)
        public double? Capacity(string sector)
        {
            double? hourly = HourlyCapacity(sector);
            return hourly == null ? (double?)null : hourly.Value * _window.TotalMinutes / 60;
        }

        // Nominal capacity per hour of the capacity file, null = no limit (999, <= 0 or not in the file)
        public double? HourlyCapacity(string sector)
        {
            if (!_hourly.TryGetValue(sector, out double hourly) || hourly >= UNLIMITED || hourly <= 0)
                return null;
            return hourly;
        }

        public CapacityStatus Status(int demand, double? capacity)
        {
            if (capacity == null || demand < NEAR_LIMIT * capacity.Value)
                return CapacityStatus.Green;
            return demand <= capacity.Value ? CapacityStatus.Yellow : CapacityStatus.Red;
        }

        private static int FirstAtOrAfter(List<DateTime> times, DateTime t)
        {
            int lo = 0, hi = times.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (times[mid] < t) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }
}
