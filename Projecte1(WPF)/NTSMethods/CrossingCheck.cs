using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.NTS
{
    // TEST of the crossing algorithm on a few flights, before running the whole batch (ComputeEntries).
    // For each flight it runs CrossingCalculator.Cross with the debug log and EntryCalculator, writes a readable report,
    // and checks Cross against an INDEPENDENT oracle: points every SAMPLE_SECONDS along each segment (linear interpolation),
    // tested one by one with NTS point-in-polygon + FL band. The oracle builds its own volumes, so it does not depend on
    // the code being tested.
    public static class CrossingCheck
    {
        public const double SAMPLE_SECONDS = 5;

        // A disagreement closer than this to the start/end of a computed interval is rounding, not an error
        public const double TOLERANCE_SECONDS = 1;

        public class FlightResult
        {
            public string CallSign;
            public Trajectory Flight;                         // null if not in the loaded trajectories
            public Dictionary<string, List<Interval>> Inside; // null if Cross failed
            public List<SectorEntry> Entries = new List<SectorEntry>();
            public int Samples, Mismatches, OpenViolations, OpenMismatches;
            public bool OpenChecked;                          // open-sector oracle ran (needs both parameters at 0)
            public string Error;
            public bool Ok { get { return Error == null && Mismatches == 0 && OpenViolations == 0 && OpenMismatches == 0; } }
        }

        private class OracleVolume
        {
            public string Sector;
            public int BottomLevel, TopLevel;
            public Envelope Envelope;
            public IndexedPointInAreaLocator Locator;
        }

        // What the open-sector oracle needs, built here and not taken from EntryCalculator
        private class OpenOracle
        {
            public List<string> Accs = new List<string>();
            public Dictionary<string, List<string>> ConfigSectors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, HashSet<string>> Members = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        public static string Run(mainProgram p, IEnumerable<string> callSigns, out List<FlightResult> results)
        {
            results = new List<FlightResult>();
            var sb = new StringBuilder();
            sb.AppendLine($"CROSSING CHECK  sample every {SAMPLE_SECONDS} s, tolerance {TOLERANCE_SECONDS} s, " +
                          $"MinTimeInSector {p.Parameters.MinTimeInSector} s, ReentryTime {p.Parameters.ReentryTime} s");
            sb.AppendLine();

            List<OracleVolume> oracle = BuildOracle(p);
            OpenOracle openOracle = BuildOpenOracle(p);
            bool paramsZero = p.Parameters.MinTimeInSector == 0 && p.Parameters.ReentryTime == 0;

            CrossingCalculator crossing = null;
            EntryCalculator entries = null;
            string setupError = null;
            try { crossing = new CrossingCalculator(p.Elementary, p.Airblocks); }
            catch (Exception ex) { setupError = "CrossingCalculator constructor: " + Describe(ex); }
            try { entries = new EntryCalculator(p.Configurations, p.Elementary, p.Collapsed, p.OpeningScheme); }
            catch (Exception ex) { sb.AppendLine("EntryCalculator constructor: " + Describe(ex)); }

            foreach (string callSign in callSigns)
            {
                var r = new FlightResult { CallSign = callSign };
                results.Add(r);
                r.Flight = p.Trajectories.FirstOrDefault(t => string.Equals(t.CallSign, callSign, StringComparison.OrdinalIgnoreCase));

                sb.AppendLine(new string('=', 100));
                if (r.Flight == null)
                {
                    r.Error = "not in the loaded trajectories (outside the bbox prefilter, or wrong call sign)";
                    sb.AppendLine($"{callSign}: {r.Error}");
                    continue;
                }

                Trajectory t = r.Flight;
                sb.AppendLine($"{t.CallSign}  id {t.FlightIdentifier}  {t.Origin} -> {t.Destination}  {t.AircraftType}  " +
                              $"{t.Segments.Count} segments  {t.Segments[0].BeginDateTime:dd/MM HH:mm:ss} - " +
                              $"{t.Segments[t.Segments.Count - 1].EndDateTime:dd/MM HH:mm:ss}");

                // 1. Cross with debug log
                var log = new List<CrossingStep>();
                if (setupError != null) r.Error = setupError;
                else
                {
                    try { r.Inside = crossing.Cross(t, log); }
                    catch (Exception ex) { r.Error = "Cross: " + Describe(ex); }
                }
                if (r.Error != null)
                {
                    sb.AppendLine("  " + r.Error);
                    continue;
                }

                sb.AppendLine("  -- segment log: 1 horizontal u_h, 2 vertical u_v, 3 both (AND) u_entry/u_exit, 4 interpolated times");
                sb.AppendLine("     (only volumes the segment enters horizontally are listed; the others are counted)");
                foreach (var group in log.GroupBy(s => s.Segment))
                {
                    TrajectorySegment s = t.Segments[group.Key];
                    int missed = group.Count(x => x.Horizontal.Count == 0);
                    sb.AppendLine($"  seg {group.Key,3} {s.PointFrom}->{s.PointTo}  {s.BeginDateTime:HH:mm:ss}-{s.EndDateTime:HH:mm:ss}  " +
                                  $"FL{s.FLBegin:000}->FL{s.FLEnd:000}  candidates {group.Count()}, outside horizontally {missed}");
                    foreach (CrossingStep x in group.Where(x => x.Horizontal.Count > 0))
                    {
                        string h = string.Join(" ", x.Horizontal);
                        string v = x.Vertical == null ? "none" : x.Vertical.Value.ToString();
                        string both = x.Both.Count == 0 ? "none" : string.Join(" ", x.Both);
                        string times = x.Times.Count == 0 ? "-" : string.Join(" ", x.Times.Select(i => $"{i.Start:HH:mm:ss}-{i.End:HH:mm:ss}"));
                        sb.AppendLine($"        {x.Sector,-12} FL[{x.FlMin:000},{x.FlMax:000})  u_h {h}  u_v {v}  both {both}  -> {times}");
                    }
                }

                sb.AppendLine("  -- elementary sector intervals (Cross result)");
                if (r.Inside.Count == 0)
                    sb.AppendLine("     none: the flight never enters the airspace of the ACCs -> it would be dropped");
                foreach (var kv in r.Inside.OrderBy(kv => kv.Value[0].Start))
                    sb.AppendLine($"     {kv.Key,-12} " + string.Join("  ", kv.Value.Select(i => i.ToString())));

                // 2. Entries of both levels
                bool openOk = false;
                if (entries != null)
                {
                    try { r.Entries.AddRange(entries.ElementaryEntries(t, r.Inside, p.Parameters)); }
                    catch (Exception ex) { sb.AppendLine("  ElementaryEntries: " + Describe(ex)); }
                    try { r.Entries.AddRange(entries.OpenEntries(t, r.Inside, p.Parameters)); openOk = true; }
                    catch (Exception ex) { sb.AppendLine("  OpenEntries: " + Describe(ex)); }
                }

                // 3. Independent oracles: elementary intervals and, with both parameters at 0, the open-sector entries
                var mismatches = new List<string>();
                var openMismatches = new List<string>();
                r.OpenChecked = openOk && paramsZero;
                CompareWithOracle(p, t, r.Inside, oracle, r.OpenChecked ? openOracle : null,
                                  r.Entries.Where(e => e.Level == EntryLevel.Open).ToList(), ref r.Samples, mismatches, openMismatches);
                r.Mismatches = mismatches.Count;
                r.OpenMismatches = openMismatches.Count;
                sb.AppendLine($"  -- oracle (elementary): {r.Samples} sample points, {r.Mismatches} disagreements");
                foreach (string m in mismatches.Take(15))
                    sb.AppendLine("     " + m);
                if (r.OpenChecked)
                {
                    sb.AppendLine($"  -- oracle (open sectors per .cos/.cfg): {r.OpenMismatches} disagreements");
                    foreach (string m in openMismatches.Take(15))
                        sb.AppendLine("     " + m);
                }
                else
                    sb.AppendLine("  -- oracle (open sectors): skipped (needs OpenEntries and MinTimeInSector = ReentryTime = 0)");
                sb.AppendLine("  -- entries");
                foreach (SectorEntry e in r.Entries.OrderBy(e => e.Level).ThenBy(e => e.Entry))
                {
                    string open = e.Level == EntryLevel.Open ? CheckOpen(p, e) : null;
                    if (open != null) r.OpenViolations++;
                    sb.AppendLine($"     {e.Level,-10} {e.Cause,-8} {e.Sector,-12} {e.Acc,-9} {e.Configuration,-8} " +
                                  $"{e.Entry:HH:mm:ss} -> {e.Exit:HH:mm:ss}  {e.DurationSeconds,6:F0} s" + (open == null ? "" : "   <-- " + open));
                }

                sb.AppendLine($"  RESULT {t.CallSign}: " + (r.Ok ? "OK" : "PROBLEMS"));
            }

            sb.AppendLine(new string('=', 100));
            sb.AppendLine("SUMMARY");
            foreach (FlightResult r in results)
                sb.AppendLine($"  {r.CallSign,-9} " + (r.Error != null ? r.Error :
                    $"{r.Inside.Count} elementary sectors, {r.Samples} samples, {r.Mismatches} disagreements | " +
                    $"{r.Entries.Count(e => e.Level == EntryLevel.Open)} open entries ({r.Entries.Count(e => e.Cause == EntryCause.Opened)} opened), " +
                    $"{r.OpenViolations} not open, " + (r.OpenChecked ? $"{r.OpenMismatches} disagreements" : "oracle skipped") +
                    " -> " + (r.Ok ? "OK" : "PROBLEMS")));
            return sb.ToString();
        }

        // Position by linear interpolation (lat, lon in degrees, FL). Clamped to the first/last point of the flight
        public static (double Latitude, double Longitude, double FL) PositionAt(Trajectory t, DateTime time)
        {
            DateTime clamped = time < t.Start ? t.Start : time > t.End ? t.End : time;
            t.TryGetPosition(clamped, out double lat, out double lon, out double fl);
            return (lat, lon, fl);
        }

        private static List<OracleVolume> BuildOracle(mainProgram p)
        {
            var list = new List<OracleVolume>();
            foreach (ElementarySector es in p.Elementary)
                foreach (BandGeometry band in AirblockMerger.MergeGeometries(es, p.Airblocks))
                    list.Add(new OracleVolume
                    {
                        Sector = es.Name,
                        BottomLevel = band.BottomLevel,
                        TopLevel = band.TopLevel,
                        Envelope = band.Shape.EnvelopeInternal,
                        Locator = new IndexedPointInAreaLocator(band.Shape)
                    });
            return list;
        }

        private static OpenOracle BuildOpenOracle(mainProgram p)
        {
            var o = new OpenOracle();
            var elementaryNames = new HashSet<string>(p.Elementary.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var csById = new Dictionary<string, Airspace>(StringComparer.OrdinalIgnoreCase);
            foreach (Airspace cs in p.Collapsed) csById[cs.Id] = cs;

            foreach (Configuration c in p.Configurations)
            {
                if (!o.Accs.Contains(c.AccName, StringComparer.OrdinalIgnoreCase))
                    o.Accs.Add(c.AccName);
                var sectors = c.Elementary.Select(e => e.Name).Concat(c.Collapsed.Select(cs => cs.Id)).ToList();
                o.ConfigSectors[c.AccName + ";" + c.ConfigurationName] = sectors;

                foreach (string s in sectors)
                {
                    if (o.Members.ContainsKey(s))
                        continue;
                    // Elementary members, walking nested collapsed sectors with an explicit stack
                    var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var stack = new Stack<string>();
                    stack.Push(s);
                    while (stack.Count > 0)
                    {
                        string n = stack.Pop();
                        if (!seen.Add(n)) continue;
                        if (csById.TryGetValue(n, out Airspace cs)) foreach (Sector m in cs.Sectors) stack.Push(m.Name.Trim());
                        else if (elementaryNames.Contains(n)) members.Add(n);
                    }
                    o.Members[s] = members;
                }
            }
            return o;
        }

        private static void CompareWithOracle(mainProgram p, Trajectory t, Dictionary<string, List<Interval>> inside,
                                              List<OracleVolume> oracle, OpenOracle open, List<SectorEntry> openEntries,
                                              ref int samples, List<string> mismatches, List<string> openMismatches)
        {
            foreach (TrajectorySegment s in t.Segments)
            {
                double span = (s.EndDateTime - s.BeginDateTime).TotalSeconds;
                if (span <= 0)
                    continue;

                for (double sec = 0; sec < span; sec += SAMPLE_SECONDS)
                {
                    double u = sec / span;
                    DateTime time = s.BeginDateTime.AddSeconds(sec);
                    double lat = s.LatBegin + u * (s.LatEnd - s.LatBegin);
                    double lon = s.LonBegin + u * (s.LonEnd - s.LonBegin);
                    double fl = s.FLBegin + u * (s.FLEnd - s.FLBegin);
                    var c = new Coordinate(lon, lat);
                    samples++;

                    // A point exactly ON a polygon edge cannot be judged (e.g. departure from the airport point where the
                    // TMA sectors meet: the flight touches them for zero time), so those sectors are skipped
                    var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var onBoundary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (OracleVolume v in oracle)
                    {
                        if (fl < v.BottomLevel || fl >= v.TopLevel || !v.Envelope.Covers(c))
                            continue;
                        Location loc = v.Locator.Locate(c);
                        if (loc == Location.Interior) expected.Add(v.Sector);
                        else if (loc == Location.Boundary) onBoundary.Add(v.Sector);
                    }
                    expected.ExceptWith(onBoundary);

                    var computed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in inside)
                        if (kv.Value.Any(i => time >= i.Start && time <= i.End))
                            computed.Add(kv.Key);

                    foreach (string sector in expected.Union(computed, StringComparer.OrdinalIgnoreCase))
                    {
                        if (expected.Contains(sector) == computed.Contains(sector) || onBoundary.Contains(sector))
                            continue;
                        if (NearIntervalEnd(inside, sector, time))
                            continue; // rounding at a boundary
                        mismatches.Add($"{time:HH:mm:ss} {sector,-12} oracle {(expected.Contains(sector) ? "INSIDE" : "outside")}, " +
                                       $"Cross {(computed.Contains(sector) ? "INSIDE" : "outside")}  " +
                                       $"(lat {lat.ToString("F4", CultureInfo.InvariantCulture)}, lon {lon.ToString("F4", CultureInfo.InvariantCulture)}, FL{fl:F0})");
                    }

                    if (open == null)
                        continue;

                    // Open sectors containing the flight now: for each ACC the configuration the .cos gives at this time,
                    // and its sectors whose elementary members contain the point
                    var expectedOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var ambiguousOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string acc in open.Accs)
                    {
                        string config = p.OpeningScheme.GetConfigurationName(acc, time);
                        if (config == null || !open.ConfigSectors.TryGetValue(acc + ";" + config, out List<string> sectors))
                            continue;
                        foreach (string sector in sectors)
                        {
                            HashSet<string> members = open.Members[sector];
                            if (members.Overlaps(expected)) expectedOpen.Add(sector);
                            else if (members.Overlaps(onBoundary)) ambiguousOpen.Add(sector);
                        }
                    }

                    var computedOpen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (SectorEntry e in openEntries)
                        if (time >= e.Entry && time <= e.Exit)
                            computedOpen.Add(e.Sector);

                    foreach (string sector in expectedOpen.Union(computedOpen, StringComparer.OrdinalIgnoreCase))
                    {
                        if (expectedOpen.Contains(sector) == computedOpen.Contains(sector) || ambiguousOpen.Contains(sector))
                            continue;
                        if (NearEntryEnd(openEntries, sector, time) ||
                            (open.Members.TryGetValue(sector, out HashSet<string> m) && m.Any(es => NearIntervalEnd(inside, es, time))))
                            continue; // rounding at a sector boundary or at an opening/closing time
                        openMismatches.Add($"{time:HH:mm:ss} {sector,-12} open+inside per .cos/.cfg: {(expectedOpen.Contains(sector) ? "YES" : "no")}, " +
                                           $"OpenEntries: {(computedOpen.Contains(sector) ? "YES" : "no")}");
                    }
                }
            }
        }

        private static bool NearEntryEnd(List<SectorEntry> entries, string sector, DateTime time)
        {
            foreach (SectorEntry e in entries)
                if (string.Equals(e.Sector, sector, StringComparison.OrdinalIgnoreCase) &&
                    (Math.Abs((time - e.Entry).TotalSeconds) <= TOLERANCE_SECONDS || Math.Abs((time - e.Exit).TotalSeconds) <= TOLERANCE_SECONDS))
                    return true;
            return false;
        }

        private static bool NearIntervalEnd(Dictionary<string, List<Interval>> inside, string sector, DateTime time)
        {
            if (!inside.TryGetValue(sector, out List<Interval> list))
                return false;
            foreach (Interval i in list)
                if (Math.Abs((time - i.Start).TotalSeconds) <= TOLERANCE_SECONDS || Math.Abs((time - i.End).TotalSeconds) <= TOLERANCE_SECONDS)
                    return true;
            return false;
        }

        // null if the entry's sector is in the configuration the opening scheme has open for its ACC at Entry; else the problem
        private static string CheckOpen(mainProgram p, SectorEntry e)
        {
            if (e.Acc == null)
                return "no ACC";
            string open = p.OpeningScheme.GetConfigurationName(e.Acc, e.Entry);
            if (!string.Equals(open, e.Configuration, StringComparison.OrdinalIgnoreCase))
                return $"open configuration at {e.Entry:HH:mm:ss} is {open ?? "none"}";
            Configuration cfg = p.FindConfiguration(e.Acc, e.Configuration);
            bool contains = cfg != null && (cfg.Elementary.Any(x => string.Equals(x.Name, e.Sector, StringComparison.OrdinalIgnoreCase)) ||
                                            cfg.Collapsed.Any(x => string.Equals(x.Id, e.Sector, StringComparison.OrdinalIgnoreCase)));
            return contains ? null : $"{e.Sector} is not in {e.Acc};{e.Configuration}";
        }

        private static string Describe(Exception ex)
        {
            return ex is NotImplementedException ? "not implemented yet (" + ex.Message + ")" : ex.GetType().Name + ": " + ex.Message;
        }
    }
}
