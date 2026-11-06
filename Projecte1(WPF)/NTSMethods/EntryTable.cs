using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Projecte1_WPF_.Simulation;

namespace Projecte1_WPF_.NTS
{
    // ENTRY RESULTS, slide "Entry result": Trajectory | Entity | Entry | Exit | Duration, then the other data we have.
    // Results > Export entries builds it with a pre-simulation (FromSimulation) and saves it with SaveCsv, as the spec
    // asks ("entry results file with at least trajectory, sector name, entry time, exit time, duration")
    public static class EntryTable
    {
        // Pre-simulation step: simulated time replayed per batch of events (only batching, no effect on the result)
        private static readonly TimeSpan STEP = TimeSpan.FromMinutes(10);

        // PRE-SIMULATION: replays the simulation from its start to its end, step by step like the timeline but without
        // drawing, and registers every entry/exit it plots: an entry dot opens a stay, its exit dot (same SectorEntry)
        // closes it and becomes one row. So the rows are exactly the dots of the simulation: entries into the relevant
        // sectors (open per the opening scheme) between start and end, whatever the timeline position. Rows by entry time.
        // progress (optional) gets the fraction of the simulated time replayed, 0..1
        public static DataTable FromSimulation(TimeSimulation simulation, IProgress<double> progress = null)
        {
            var inside = new Dictionary<SectorEntry, DateTime>(); // entry dot seen, exit dot not yet
            var stays = new List<(SectorEntry Entry, DateTime In, DateTime Out)>();
            double total = (simulation.End - simulation.Start).TotalSeconds;

            DateTime t = simulation.Start.AddTicks(-1);
            while (t < simulation.End)
            {
                DateTime next = t + STEP < simulation.End ? t + STEP : simulation.End;
                List<SimEvent> events = simulation.EventsBetween(t, next);

                // Entries first, then exits: a stay that starts and ends inside the same step (even at the same instant)
                // is always opened before it is closed
                foreach (SimEvent ev in events)
                    if (ev.IsEntry)
                        inside[ev.Source] = ev.Time;
                foreach (SimEvent ev in events)
                    if (!ev.IsEntry && inside.TryGetValue(ev.Source, out DateTime entered))
                    {
                        inside.Remove(ev.Source);
                        stays.Add((ev.Source, entered, ev.Time));
                    }
                t = next;
                progress?.Report(total > 0 ? (t - simulation.Start).TotalSeconds / total : 1);
            }

            // Day of the month when the whole simulation is in one month (fits Excel's default column width, a full date
            // shows as #####); otherwise the full date
            bool oneMonth = simulation.Start.Year == simulation.End.Year && simulation.Start.Month == simulation.End.Month;
            string Day(DateTime d) => oneMonth ? d.Day.ToString(CultureInfo.InvariantCulture)
                                               : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            DataTable table = NewTable();
            table.ExtendedProperties["Dates"] = oneMonth
                ? simulation.Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture) + " UTC, Entry day and Exit day = day of the month"
                : "UTC, Entry day and Exit day = yyyy-MM-dd";
            table.BeginLoadData();
            foreach (var s in stays.OrderBy(x => x.In))
            {
                DateTime entry = RoundToSecond(s.In), exit = RoundToSecond(s.Out);
                table.Rows.Add(s.Entry.FlightId, s.Entry.Sector, Day(entry), entry.TimeOfDay, Day(exit), exit.TimeOfDay,
                               (int)(exit - entry).TotalSeconds, s.Entry.CallSign, s.Entry.Acc, s.Entry.Configuration,
                               s.Entry.Cause.ToString());
            }
            table.EndLoadData();
            return table;
        }

        // Entry and exit are split into day and time of day (two columns each), UTC, rounded to the second
        private static DataTable NewTable()
        {
            var table = new DataTable("Entries");
            table.Columns.Add("Trajectory", typeof(long));     // .so6 flight identifier
            table.Columns.Add("Entity", typeof(string));        // sector entered: elementary or collapsed, open at that time
            table.Columns.Add("EntryDay", typeof(string)).Caption = "Entry day";
            table.Columns.Add("EntryTime", typeof(TimeSpan)).Caption = "Entry time";
            table.Columns.Add("ExitDay", typeof(string)).Caption = "Exit day";
            table.Columns.Add("ExitTime", typeof(TimeSpan)).Caption = "Exit time";
            table.Columns.Add("Duration", typeof(int)).Caption = "Duration (s)"; // Exit - Entry, seconds
            table.Columns.Add("CallSign", typeof(string));
            table.Columns.Add("ACC", typeof(string));
            table.Columns.Add("Configuration", typeof(string)); // configuration open in that ACC at Entry
            table.Columns.Add("Cause", typeof(string));         // Crossing, or Opened: already inside when the sector opened
            return table;
        }

        // ';'-separated like the input files (Excel in Spanish opens it in columns). First the parameter lines, each
        // "# name;value" (Excel shows the name in column A and the value in B; other readers skip lines starting with '#'),
        // then a blank line, the header (column captions) and the rows. Times "HH:mm:ss" UTC
        public static void SaveCsv(DataTable table, string path, IEnumerable<(string Name, string Value)> parameters)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                foreach (var (name, value) in parameters)
                    writer.WriteLine("# " + name + ";" + value);
                if (table.ExtendedProperties["Dates"] is string dates)
                    writer.WriteLine("# Dates;" + dates);
                writer.WriteLine();
                writer.WriteLine(string.Join(";", table.Columns.Cast<DataColumn>().Select(c => c.Caption)));
                foreach (DataRow row in table.Rows)
                    writer.WriteLine(string.Join(";", row.ItemArray.Select(Format)));
            }
        }

        private static string Format(object value)
        {
            if (value is TimeSpan s) return s.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            if (value is IFormattable f) return f.ToString(null, CultureInfo.InvariantCulture);
            return value == null || value == DBNull.Value ? "" : value.ToString();
        }

        private static DateTime RoundToSecond(DateTime t)
        {
            return new DateTime((t.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, t.Kind);
        }
    }
}
