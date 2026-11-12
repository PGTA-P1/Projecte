using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.NTS;
using Projecte1_WPF_.ReadData;

namespace Projecte1_WPF_
{
    public class mainProgram
    {
        public List<Airblock> Airblocks { get; } = new List<Airblock>();
        public List<ElementarySector> Elementary { get; } = new List<ElementarySector>();

        // Collapsed sectors: the 'A' records of the .spc whose type is CS (their 'S' lines are elementary sectors)
        public List<Airspace> Collapsed { get; } = new List<Airspace>();

        // Configurations of the .cfg, each with its elementary and collapsed sectors. Needs LoadElementary and LoadCollapsed first
        public List<Configuration> Configurations { get; } = new List<Configuration>();

        // ACCs read from accs.txt by LoadFiltered (null if LoadFiltered has not been called)
        public HashSet<string> Accs { get; private set; }

        // Which configuration each ACC has open at each moment (.cos)
        public OpeningScheme OpeningScheme { get; private set; }

        // Hourly capacity of each sector (capacity_sector_level.csv)
        public List<CapacityRecord> Capacities { get; } = new List<CapacityRecord>();

        // Flights of the .so6
        public List<Trajectory> Trajectories { get; } = new List<Trajectory>();

        // Box of the kept airblocks used to filter the .so6 (null if LoadFiltered has not been called)
        public LatLonBox Area { get; private set; }

        // Margin added around the airblocks when filtering trajectories (degrees, 0.5 deg ~ 55 km)
        private const double AREA_MARGIN_DEG = 0.5;

        // Project parameters (minimum time in sector, reentry time) used by ComputeEntries
        public EntryParameters Parameters { get; } = new EntryParameters();

        // Project parameters of the demand (integration window, sliding step), used by Simulation/SectorDemand
        public Simulation.DemandParameters DemandParameters { get; } = new Simulation.DemandParameters();

        // Entries of both levels (Elementary and Open), sorted by entry time. Filled by ComputeEntries
        public List<SectorEntry> Entries { get; private set; } = new List<SectorEntry>();

        // Trajectories that really enter an elementary sector of the ACCs (exact filter), sorted by start time.
        // Filled by ComputeEntries; Trajectories keeps everything loaded (the crossing check also looks at dropped flights)
        public List<Trajectory> RelevantTrajectories { get; private set; } = new List<Trajectory>();

        // ------------------------------------------------------------------------------------------------
        // ENTRIES of the whole batch. Needs a load first (LoadFiltered).
        // NTSMethods/CrossingCheck.Run checks a few flights against independent oracles (no menu any more: call it from a
        // test harness after touching the crossing code).
        // Results are built in new lists and published at the end, so it can run on a background thread.
        // ------------------------------------------------------------------------------------------------
        // Returns a one-line summary (counts and time). progress (optional) gets the fraction of trajectories done, 0..1
        public string ComputeEntries(IProgress<double> progress = null)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var crossing = new CrossingCalculator(Elementary, Airblocks);
            var calculator = new EntryCalculator(Configurations, Elementary, Collapsed, OpeningScheme);

            // Flights are independent, so they run in parallel (both calculators only read once built). Each flight
            // writes its own slot and the slots are joined in load order: same result and order as one after another
            var perFlight = new List<SectorEntry>[Trajectories.Count];
            int done = 0;
            Parallel.For(0, Trajectories.Count, i =>
            {
                Trajectory t = Trajectories[i];
                Dictionary<string, List<Interval>> inside = crossing.Cross(t);
                // Empty: never inside an elementary sector of the ACCs, does not cross their airspace (exact filter)
                if (inside.Count > 0)
                {
                    List<SectorEntry> list = calculator.ElementaryEntries(t, inside, Parameters);
                    list.AddRange(calculator.OpenEntries(t, inside, Parameters));
                    perFlight[i] = list;
                }
                int n = Interlocked.Increment(ref done);
                if (progress != null && n % 50 == 0)
                    progress.Report((double)n / Trajectories.Count);
            });

            var entries = new List<SectorEntry>();
            var crossed = new List<Trajectory>();
            for (int i = 0; i < perFlight.Length; i++)
            {
                if (perFlight[i] == null)
                    continue;
                crossed.Add(Trajectories[i]);
                entries.AddRange(perFlight[i]);
            }

            entries.Sort((a, b) => a.Entry.CompareTo(b.Entry));
            crossed.Sort((a, b) => a.Start.CompareTo(b.Start));
            Entries = entries;
            RelevantTrajectories = crossed;

            int nElementary = 0, nCrossing = 0, nOpened = 0;
            foreach (SectorEntry e in entries)
            {
                if (e.Level == EntryLevel.Elementary) nElementary++;
                else if (e.Cause == EntryCause.Crossing) nCrossing++;
                else nOpened++;
            }
            int dropped = Trajectories.Count - crossed.Count;
            return $"{crossed.Count} trajectories cross the airspace ({dropped} dropped). Entries: {nElementary} elementary, " +
                   $"{nCrossing + nOpened} open ({nCrossing} crossing, {nOpened} opened while inside). " +
                   $"{crossing.Volumes.Count} volumes, {watch.ElapsedMilliseconds} ms";
        }

        private static string Archive(string file)
        {
            return Path.Combine(AppContext.BaseDirectory, "Archives", file);
        }

        // ------------------------------------------------------------------------------------------------
        // FILTERED LOAD: only what the ACCs of accs.txt need stays in memory.
        // Order: ACCs -> sector names in the .cfg -> collapsed (.spc, nested too) -> elementary (.sls) -> airblocks (.are) -> configurations
        //        -> opening scheme (.cos) -> capacities (.csv) -> trajectories crossing the airblocks' box (.so6)
        // ------------------------------------------------------------------------------------------------
        // Problems of the last load that do not stop it (e.g. an ACC without configurations), for the status line
        public List<string> LoadWarnings { get; } = new List<string>();

        // The files of the last load (null before loading)
        public DataFiles Files { get; private set; }

        // files = null: the default files of the Archives folder. Throws DataFileException (file, line, what is wrong) when a
        // file cannot be read, or when the files do not fit together (e.g. no ACC of accs.txt has a configuration)
        public void LoadFiltered(DataFiles files = null)
        {
            files = files ?? DataFiles.Default();
            Files = files;
            LoadWarnings.Clear();
            Accs = DataFiles.ReadAccs(files[DataRole.Accs]);
            string cfgPath = files[DataRole.Configurations];

            // 1. Sector names used by those ACCs in the .cfg (names only, nothing else stored)
            var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in File.ReadLines(cfgPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                string[] p = line.Split(';');
                if (p.Length >= 3 && Accs.Contains(p[0].Trim())) needed.Add(p[2].Trim());
            }
            if (needed.Count == 0)
                throw new DataFileException(cfgPath, $"no configuration of the ACCs of {Path.GetFileName(files[DataRole.Accs])} " +
                                                     $"({string.Join(", ", Accs.Take(5))}{(Accs.Count > 5 ? ", …" : "")}) in this file");

            // 2. Collapsed sectors, following nested ones (a CS can contain other CS)
            SpcFile spc = new SpcReader().Read(files[DataRole.Airspaces]);
            var csById = new Dictionary<string, Airspace>(StringComparer.OrdinalIgnoreCase);
            foreach (Airspace a in spc.Airspaces)
                if (a.Type == "CS") csById[a.Id] = a;

            Collapsed.Clear();
            var queue = new Queue<string>(needed);
            while (queue.Count > 0)
            {
                if (!csById.TryGetValue(queue.Dequeue(), out Airspace cs)) continue;
                Collapsed.Add(cs);
                foreach (Sector m in cs.Sectors)
                    if (needed.Add(m.Name.Trim())) queue.Enqueue(m.Name.Trim());
            }
            // (spc and csById go out of scope here, so the unused airspaces can be collected)

            // 3. Elementary sectors
            Elementary.Clear();
            Elementary.AddRange(Read_ElementarySectors.LoadFromFile(files[DataRole.ElementarySectors], needed));
            if (Elementary.Count == 0)
                throw new DataFileException(files[DataRole.ElementarySectors], "none of the sectors used by the configurations of the ACCs is in this file");

            // 4. Airblocks used by those elementary sectors
            var blocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ElementarySector es in Elementary)
                foreach (SectorVolume v in es.Volumes) blocks.Add(v.VolumeName);
            Airblocks.Clear();
            Airblocks.AddRange(new Reader_Airblock().ReadFromFile(files[DataRole.Airblocks], blocks));
            if (Airblocks.Count == 0)
                throw new DataFileException(files[DataRole.Airblocks], "none of the airblocks used by the elementary sectors is in this file");
            int missingBlocks = blocks.Count - Airblocks.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (missingBlocks > 0)
                LoadWarnings.Add($"{missingBlocks} airblocks used by the sectors are not in {Path.GetFileName(files[DataRole.Airblocks])}");

            // 5. Configurations of those ACCs
            Configurations.Clear();
            Configurations.AddRange(new ConfigurationReader()
                .Read(cfgPath, Elementary, Collapsed, Accs).Configurations);
            var withoutConfig = Accs.Where(a => !Configurations.Any(c => string.Equals(c.AccName, a, StringComparison.OrdinalIgnoreCase))).ToList();
            if (withoutConfig.Count > 0)
                LoadWarnings.Add($"no configuration for {string.Join(", ", withoutConfig)}");
            int notFound = Configurations.Sum(c => c.NotFound.Count);
            if (notFound > 0)
                LoadWarnings.Add($"{notFound} sector names of the configurations are in neither the .sls nor the .spc");

            // 6. Opening scheme of those ACCs
            OpeningScheme = Read_OpeningScheme.ReadFile(files[DataRole.OpeningScheme], Accs);
            if (OpeningScheme.Records.Count == 0)
                throw new DataFileException(files[DataRole.OpeningScheme], $"no opening record for the ACCs ({string.Join(", ", Accs.Take(5))}{(Accs.Count > 5 ? ", …" : "")})");

            // 7. Capacities of the kept sectors (elementary and collapsed) and of the ACCs themselves
            var capacityNames = new HashSet<string>(needed, StringComparer.OrdinalIgnoreCase);
            capacityNames.UnionWith(Accs);
            Capacities.Clear();
            Capacities.AddRange(new CapacityReader().Read(files[DataRole.Capacities], capacityNames).Capacities);
            if (Capacities.Count == 0)
                LoadWarnings.Add("no capacity for these sectors: every sector counts as without limit (always green)");

            // 8. Trajectories with some segment inside the box of the kept airblocks
            Area = LatLonBox.FromAirblocks(Airblocks, AREA_MARGIN_DEG);
            Trajectories.Clear();
            if (Area != null)
                Trajectories.AddRange(new Reader_So6().ReadFromFile(files[DataRole.Trajectories], Area));
            if (Trajectories.Count == 0)
                throw new DataFileException(files[DataRole.Trajectories], "no flight crosses the area of the ACCs");
        }

        // ------------------------------------------------------------------------------------------------
        // TEST: compares the filtered load with the full (unfiltered) load. Returns a text report ("RESULT: OK" if all is correct)
        // ------------------------------------------------------------------------------------------------
        private static string Sig(Configuration c)
        {
            int vols = 0, members = 0;
            foreach (ElementarySector es in c.Elementary) vols += es.Volumes.Count;
            foreach (Airspace cs in c.Collapsed) members += cs.Sectors.Count;
            return c.Elementary.Count + "/" + vols + "/" + c.Collapsed.Count + "/" + members + "/" + c.NotFound.Count;
        }

        public string TestFilter()
        {
            var sb = new StringBuilder();

            // Baseline first, so data already loaded elsewhere (e.g. the window's own mainProgram) is not counted
            long memStart = GC.GetTotalMemory(true);
            LoadFiltered();
            long memFiltered = GC.GetTotalMemory(true) - memStart;

            var full = new mainProgram();
            full.LoadElementary(); full.LoadCollapsed(); full.LoadAirblocks(); full.LoadConfiguration();
            full.LoadOpeningScheme(); full.LoadCapacities(); full.LoadTrajectories();
            long memFull = GC.GetTotalMemory(true) - memStart - memFiltered;

            int bad = 0;

            // a) nothing from other ACCs, and each kept configuration identical to the unfiltered one
            foreach (Configuration c in Configurations)
            {
                if (!Accs.Contains(c.AccName)) { bad++; sb.AppendLine("LEAK: " + c.AccName + ";" + c.ConfigurationName); }
                Configuration f = full.FindConfiguration(c.AccName, c.ConfigurationName);
                if (f == null || Sig(f) != Sig(c))
                {
                    bad++;
                    sb.AppendLine("DIFF: " + c.AccName + ";" + c.ConfigurationName + "  filtered=" + Sig(c) + "  full=" + (f == null ? "null" : Sig(f)));
                }
            }

            // b) no configuration of the chosen ACCs was lost
            int expected = 0;
            foreach (Configuration c in full.Configurations) if (Accs.Contains(c.AccName)) expected++;
            if (expected != Configurations.Count) { bad++; sb.AppendLine("COUNT: expected " + expected + ", got " + Configurations.Count); }

            // c) every volume of a kept sector still finds its airblock
            var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Airblock a in Airblocks) have.Add(a.Name);
            int missingBlocks = 0;
            foreach (ElementarySector es in Elementary)
                foreach (SectorVolume v in es.Volumes) if (!have.Contains(v.VolumeName)) missingBlocks++;

            // d) opening scheme: only the chosen ACCs, and all of their records
            int expectedRecords = 0;
            foreach (OpeningSchemeRecord r in full.OpeningScheme.Records) if (Accs.Contains(r.AccName)) expectedRecords++;
            foreach (OpeningSchemeRecord r in OpeningScheme.Records)
                if (!Accs.Contains(r.AccName)) { bad++; sb.AppendLine("LEAK .cos: " + r.AccName); break; }
            if (expectedRecords != OpeningScheme.Records.Count)
            { bad++; sb.AppendLine("COUNT .cos: expected " + expectedRecords + ", got " + OpeningScheme.Records.Count); }

            // e) capacities: only kept sectors / ACCs, and none of them lost
            var keptNames = new HashSet<string>(Accs, StringComparer.OrdinalIgnoreCase);
            foreach (ElementarySector es in Elementary) keptNames.Add(es.Name);
            foreach (Airspace cs in Collapsed) keptNames.Add(cs.Id);
            var allowedNames = new HashSet<string>(keptNames, StringComparer.OrdinalIgnoreCase);
            foreach (Configuration c in Configurations) allowedNames.UnionWith(c.NotFound);

            var haveCapacity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CapacityRecord r in Capacities)
            {
                haveCapacity.Add(r.SectorName);
                if (!allowedNames.Contains(r.SectorName)) { bad++; sb.AppendLine("LEAK capacity: " + r.SectorName); }
            }
            int lostCapacities = 0;
            foreach (CapacityRecord r in full.Capacities)
                if (keptNames.Contains(r.SectorName) && !haveCapacity.Contains(r.SectorName)) lostCapacities++;
            if (lostCapacities > 0) { bad++; sb.AppendLine("LOST capacities: " + lostCapacities); }

            // f) trajectories: exactly the ones of the full file with some segment inside Area
            var expectedFlights = new HashSet<long>();
            foreach (Trajectory t in full.Trajectories)
                foreach (TrajectorySegment s in t.Segments)
                    if (Area.Intersects(s.LatBegin, s.LonBegin, s.LatEnd, s.LonEnd)) { expectedFlights.Add(t.FlightIdentifier); break; }
            var gotFlights = new HashSet<long>();
            foreach (Trajectory t in Trajectories) gotFlights.Add(t.FlightIdentifier);
            if (!gotFlights.SetEquals(expectedFlights))
            { bad++; sb.AppendLine("DIFF .so6: expected " + expectedFlights.Count + " flights, got " + gotFlights.Count); }

            sb.AppendLine("Configurations: " + Configurations.Count + " (full file: " + full.Configurations.Count + ")");
            sb.AppendLine("Elementary: " + Elementary.Count + " / " + full.Elementary.Count);
            sb.AppendLine("Collapsed: " + Collapsed.Count + " / " + full.Collapsed.Count);
            sb.AppendLine("Airblocks: " + Airblocks.Count + " / " + full.Airblocks.Count);
            sb.AppendLine("Volumes with missing airblock: " + missingBlocks);
            sb.AppendLine("Opening scheme records: " + OpeningScheme.Records.Count + " / " + full.OpeningScheme.Records.Count);
            sb.AppendLine("Capacities: " + Capacities.Count + " / " + full.Capacities.Count);
            sb.AppendLine("Trajectories: " + Trajectories.Count + " / " + full.Trajectories.Count +
                          $"  (area lat {Area.MinLat:F1}..{Area.MaxLat:F1}, lon {Area.MinLon:F1}..{Area.MaxLon:F1})");
            sb.AppendLine("Memory: filtered " + memFiltered / 1048576 + " MB, full " + memFull / 1048576 + " MB");
            sb.AppendLine(bad == 0 ? "RESULT: OK" : "RESULT: " + bad + " problems");
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------
        // Unfiltered loads (unchanged)
        // ------------------------------------------------------------------------------------------------
        public void LoadConfiguration()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "Configuration_1608.cfg");
            Configurations.Clear();
            Configurations.AddRange(new ConfigurationReader().Read(path, Elementary, Collapsed).Configurations);
        }

        // A configuration name repeats across ACCs, so it is looked up by ACC + name (e.g. "LEBLTMA", "2AE")
        public Configuration FindConfiguration(string accName, string configName)
        {
            for (int i = 0; i < Configurations.Count; i++)
            {
                if (string.Equals(Configurations[i].AccName, accName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Configurations[i].ConfigurationName, configName, StringComparison.OrdinalIgnoreCase))
                    return Configurations[i];
            }
            return null;
        }

        public void LoadAirblocks()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "sectors_1608.are");
            Airblocks.Clear();
            Airblocks.AddRange(new Reader_Airblock().ReadFromFile(path));
        }

        public void LoadElementary()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "sectors_1608.sls");
            Elementary.Clear();
            Elementary.AddRange(Read_ElementarySectors.LoadFromFile(path));
        }

        public void LoadOpeningScheme()
        {
            OpeningScheme = Read_OpeningScheme.ReadFile(Archive("OpeningScheme_1608.cos"));
        }

        public void LoadCapacities()
        {
            Capacities.Clear();
            Capacities.AddRange(new CapacityReader().Read(Archive("capacity_sector_level.csv")).Capacities);
        }

        public void LoadTrajectories()
        {
            Trajectories.Clear();
            Trajectories.AddRange(new Reader_So6().ReadFromFile(Archive("20160728Initial_initial.so6")));
        }

        public void LoadCollapsed()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "Airspace_1608.spc");
            SpcFile spc = new SpcReader().Read(path);

            Collapsed.Clear();
            for (int i = 0; i < spc.Airspaces.Count; i++)
            {
                if (spc.Airspaces[i].Type == "CS")
                    Collapsed.Add(spc.Airspaces[i]);
            }
        }
    }
}