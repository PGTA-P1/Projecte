using System;
using System.Collections.Generic;
using System.Linq;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.Simulation
{
    // What the map shows over the whole simulation, sampled every sliding step (from the first window start), for the
    // analysis views (sector panel chart, demand heat map, traffic over time). No UI. At each sample time t, as the map:
    //   per sector: open or not (configuration of its ACC at t), entries in the window shown at t (SectorDemand.WindowAt),
    //               status against the capacity
    //   overall: aircraft in the airspace, open sectors, near the limit, overloaded
    // Built in the background once per simulation/demand (about a second: one PositionsAt per sample)
    public class AirspaceTimeline
    {
        public const sbyte CLOSED = -1;

        public List<DateTime> Starts { get; } = new List<DateTime>();     // sample times, one per step
        public List<(DateTime Start, DateTime End)> Windows { get; } = new List<(DateTime, DateTime)>(); // window shown at each sample
        public List<string> Sectors { get; } = new List<string>();        // rows: every sector open at some sample, by ACC then name
        public List<string> SectorAcc { get; } = new List<string>();      // ACC of each row
        public List<double?> Capacity { get; } = new List<double?>();     // capacity per window of each row, null = no limit
        public int[] EntryCount { get; private set; }                      // entries into each row's sector in the simulation
        public double[] AverageMinutes { get; private set; }               // average time inside per entry (minutes)
        public sbyte[,] Status { get; private set; }                       // [sector, sample]: CLOSED or (sbyte)CapacityStatus
        public int[,] Demand { get; private set; }                         // [sector, sample]: entries in the window (0 when closed)
        public int[] Aircraft { get; private set; }                        // aircraft in the airspace at the sample time
        public int[] Open { get; private set; }
        public int[] Near { get; private set; }
        public int[] Over { get; private set; }
        public TimeSpan Window { get; }
        public TimeSpan Step { get; }

        public AirspaceTimeline(mainProgram program, TimeSimulation simulation, SectorDemand demand)
        {
            Window = demand.Window;
            Step = demand.Step;
            for (DateTime s = demand.FirstStart; s < simulation.End; s += demand.Step)
            {
                Starts.Add(s);
                Windows.Add(demand.WindowAt(s));
            }

            // Sectors open at each start, per ACC (configuration from the opening scheme)
            var openAt = new List<HashSet<string>>();
            var accOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DateTime s in Starts)
            {
                var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string acc in program.Accs)
                {
                    string name = program.OpeningScheme.GetConfigurationName(acc, s);
                    Configuration cfg = name == null ? null : program.FindConfiguration(acc, name);
                    if (cfg == null)
                        continue;
                    foreach (string sector in cfg.Elementary.Select(e => e.Name).Concat(cfg.Collapsed.Select(c => c.Id)))
                    {
                        open.Add(sector);
                        if (!accOf.ContainsKey(sector))
                            accOf[sector] = acc;
                    }
                }
                openAt.Add(open);
            }
            foreach (var kv in accOf.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase).ThenBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Sectors.Add(kv.Key);
                SectorAcc.Add(kv.Value);
                Capacity.Add(demand.Capacity(kv.Key));
            }

            // Entries and time inside per sector (Level Open, as the demand)
            EntryCount = new int[Sectors.Count];
            AverageMinutes = new double[Sectors.Count];
            var rowOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < Sectors.Count; i++)
                rowOf[Sectors[i]] = i;
            foreach (var e in program.Entries)
                if (e.Level == NTS.EntryLevel.Open && rowOf.TryGetValue(e.Sector, out int r))
                {
                    EntryCount[r]++;
                    AverageMinutes[r] += (e.Exit - e.Entry).TotalMinutes;
                }
            for (int i = 0; i < Sectors.Count; i++)
                if (EntryCount[i] > 0)
                    AverageMinutes[i] /= EntryCount[i];

            int n = Starts.Count;
            Status = new sbyte[Sectors.Count, n];
            Demand = new int[Sectors.Count, n];
            Open = new int[n];
            Near = new int[n];
            Over = new int[n];
            Aircraft = new int[n];
            for (int i = 0; i < Sectors.Count; i++)
            {
                double? capacity = demand.Capacity(Sectors[i]);
                for (int k = 0; k < n; k++)
                {
                    if (!openAt[k].Contains(Sectors[i]))
                    {
                        Status[i, k] = CLOSED;
                        continue;
                    }
                    int d = demand.Demand(Sectors[i], Windows[k].Start, Windows[k].End);
                    Demand[i, k] = d;
                    CapacityStatus status = demand.Status(d, capacity);
                    Status[i, k] = (sbyte)status;
                    Open[k]++;
                    if (status == CapacityStatus.Yellow) Near[k]++;
                    else if (status == CapacityStatus.Red) Over[k]++;
                }
            }
            for (int k = 0; k < n; k++)
                Aircraft[k] = simulation.PositionsAt(Starts[k]).Count;
        }

        // Sample at or just before time t (clamped to the samples)
        public int IndexAt(DateTime t)
        {
            int k = (int)Math.Floor((t - Starts[0]).Ticks / (double)Step.Ticks);
            return Math.Max(0, Math.Min(k, Starts.Count - 1));
        }

        public int RowOf(string sector) => Sectors.FindIndex(s => string.Equals(s, sector, StringComparison.OrdinalIgnoreCase));

        // Demand / capacity of a row at a sample, -1 if closed or no limit
        public double Load(int row, int k) =>
            Status[row, k] == CLOSED || Capacity[row] == null ? -1 : Demand[row, k] / Capacity[row].Value;

        // The open sector with the highest load at a sample (-1 if none has a limit)
        public int MostLoaded(int k)
        {
            int best = -1;
            for (int i = 0; i < Sectors.Count; i++)
                if (Load(i, k) >= 0 && (best < 0 || Load(i, k) > Load(best, k)))
                    best = i;
            return best;
        }

        // OVERLOADS: consecutive red samples of a sector joined into one episode [From, To), with its peak
        public class Overload
        {
            public string Sector { get; set; }
            public string Acc { get; set; }
            public DateTime From { get; set; }
            public DateTime To { get; set; }
            public TimeSpan Duration => To - From;
            public string DurationText => Duration.TotalHours >= 1 ? $"{(int)Duration.TotalHours} h {Duration.Minutes:00} min" : $"{Duration.Minutes} min";
            public int PeakDemand { get; set; }
            public double CapacityPerWindow { get; set; }
            public double PeakLoad { get; set; }   // demand / capacity, > 1
            public string PeakLoadText => (PeakLoad * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " %";
            public DateTime PeakWindowStart { get; set; }
            public DateTime PeakWindowEnd { get; set; }
            public string PeakWindowText => $"{PeakWindowStart:HH:mm}-{PeakWindowEnd:HH:mm}";
        }

        public List<Overload> Overloads()
        {
            var list = new List<Overload>();
            for (int i = 0; i < Sectors.Count; i++)
            {
                Overload current = null;
                for (int k = 0; k <= Starts.Count; k++)
                {
                    bool red = k < Starts.Count && Status[i, k] == (sbyte)CapacityStatus.Red;
                    if (red)
                    {
                        if (current == null)
                            list.Add(current = new Overload { Sector = Sectors[i], Acc = SectorAcc[i], From = Starts[k], CapacityPerWindow = Capacity[i] ?? 0 });
                        current.To = Starts[k] + Step;
                        if (Demand[i, k] > current.PeakDemand)
                        {
                            current.PeakDemand = Demand[i, k];
                            current.PeakLoad = Load(i, k);
                            current.PeakWindowStart = Windows[k].Start;
                            current.PeakWindowEnd = Windows[k].End;
                        }
                    }
                    else
                        current = null;
                }
            }
            return list;
        }

        // Samples of a row with a status (time in that status = count x Step)
        public int Count(int row, Func<sbyte, bool> status)
        {
            int c = 0;
            for (int k = 0; k < Starts.Count; k++)
                if (status(Status[row, k]))
                    c++;
            return c;
        }
    }
}
