using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Projecte1_WPF_.Simulation
{
    // CSV EXPORTS of the analysed data (Results menu and the overload report), from the AirspaceTimeline: same format as
    // entries.csv, ';'-separated, the parameter lines "# name;value" first, then a blank line, the header and the rows.
    // Times "yyyy-MM-dd HH:mm" UTC (the simulation spans several days). Each returns the number of rows written
    public static class AnalysisExport
    {
        public const string DEMAND_CSV = "demand.csv", TRAFFIC_CSV = "traffic.csv", SECTORS_CSV = "sectors.csv", OVERLOADS_CSV = "overloads.csv";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string T(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", Inv);
        private static string N(double v, string format = "0.#") => v.ToString(format, Inv);
        private static string Status(sbyte s) => s == (sbyte)CapacityStatus.Red ? "overloaded" : s == (sbyte)CapacityStatus.Yellow ? "near limit" : "under 80 %";

        private static int Write(string path, IEnumerable<(string Name, string Value)> parameters, string header, IEnumerable<string> rows)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            int count = 0;
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                foreach (var (name, value) in parameters)
                    writer.WriteLine("# " + name + ";" + value);
                writer.WriteLine();
                writer.WriteLine(header);
                foreach (string row in rows)
                {
                    writer.WriteLine(row);
                    count++;
                }
            }
            return count;
        }

        // Demand of every open sector at every sliding step: what the map and the sector list show at that time
        public static int Demand(AirspaceTimeline tl, string path, IEnumerable<(string, string)> parameters)
        {
            IEnumerable<string> Rows()
            {
                for (int k = 0; k < tl.Starts.Count; k++)
                    for (int i = 0; i < tl.Sectors.Count; i++)
                    {
                        sbyte s = tl.Status[i, k];
                        if (s == AirspaceTimeline.CLOSED)
                            continue;
                        double? cap = tl.Capacity[i];
                        yield return string.Join(";", T(tl.Starts[k]), T(tl.Windows[k].Start), T(tl.Windows[k].End), tl.Sectors[i], tl.SectorAcc[i],
                                                 tl.Demand[i, k], cap == null ? "no limit" : N(cap.Value),
                                                 cap == null ? "" : N(tl.Load(i, k) * 100, "0"), Status(s));
                    }
            }
            return Write(path, parameters, "Time;Window start;Window end;Sector;ACC;Demand (entries);Capacity per window;Load (%);Status", Rows());
        }

        // Whole airspace at every sliding step
        public static int Traffic(AirspaceTimeline tl, string path, IEnumerable<(string, string)> parameters)
        {
            IEnumerable<string> Rows()
            {
                for (int k = 0; k < tl.Starts.Count; k++)
                {
                    int top = tl.MostLoaded(k);
                    yield return string.Join(";", T(tl.Starts[k]), T(tl.Windows[k].Start), T(tl.Windows[k].End), tl.Aircraft[k], tl.Open[k],
                                             tl.Open[k] - tl.Near[k] - tl.Over[k], tl.Near[k], tl.Over[k],
                                             top < 0 ? "" : tl.Sectors[top], top < 0 ? "" : N(tl.Load(top, k) * 100, "0"));
                }
            }
            return Write(path, parameters, "Time;Window start;Window end;Aircraft;Open sectors;Under 80 %;Near limit;Overloaded;Most loaded sector;Its load (%)", Rows());
        }

        // One row per sector over the whole simulation (the Busiest sectors tab)
        public static int Sectors(AirspaceTimeline tl, string path, IEnumerable<(string, string)> parameters)
        {
            double step = tl.Step.TotalMinutes;
            IEnumerable<string> Rows()
            {
                for (int i = 0; i < tl.Sectors.Count; i++)
                {
                    int peak = -1;
                    for (int k = 0; k < tl.Starts.Count; k++)
                        if (tl.Load(i, k) >= 0 && (peak < 0 || tl.Load(i, k) > tl.Load(i, peak)))
                            peak = k;
                    yield return string.Join(";", tl.Sectors[i], tl.SectorAcc[i], tl.Capacity[i] == null ? "no limit" : N(tl.Capacity[i].Value),
                                             tl.EntryCount[i], N(tl.AverageMinutes[i], "0.0"),
                                             N(tl.Count(i, s => s != AirspaceTimeline.CLOSED) * step, "0"),
                                             N(tl.Count(i, s => s == (sbyte)CapacityStatus.Yellow) * step, "0"),
                                             N(tl.Count(i, s => s == (sbyte)CapacityStatus.Red) * step, "0"),
                                             peak < 0 ? "" : N(tl.Load(i, peak) * 100, "0"), peak < 0 ? "" : T(tl.Starts[peak]));
                }
            }
            return Write(path, parameters, "Sector;ACC;Capacity per window;Entries;Average time inside (min);Open (min);Near limit (min);Overloaded (min);Peak load (%);Peak at", Rows());
        }

        // Every overload episode (Overload report tab), in time order
        public static int Overloads(AirspaceTimeline tl, string path, IEnumerable<(string, string)> parameters)
        {
            var rows = tl.Overloads().OrderBy(o => o.From).ThenBy(o => o.Sector)
                         .Select(o => string.Join(";", o.Sector, o.Acc, T(o.From), T(o.To), N(o.Duration.TotalMinutes, "0"), o.PeakDemand,
                                                  N(o.CapacityPerWindow), N(o.PeakLoad * 100, "0"), o.PeakWindowText));
            return Write(path, parameters, "Sector;ACC;From;To;Duration (min);Peak entries;Capacity per window;Peak load (%);Peak window", rows);
        }
    }
}
