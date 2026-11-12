using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ReadData
{
    // The eight inputs of a data set
    public enum DataRole { Airblocks, ElementarySectors, Airspaces, Configurations, OpeningScheme, Trajectories, Capacities, Accs }

    // One input: what it is, the extension that identifies it, the default file in Archives
    public class DataRoleInfo
    {
        public DataRole Role { get; set; }
        public string Title { get; set; }
        public string Extension { get; set; }
        public string DefaultFile { get; set; }
        public string Description { get; set; }
    }

    // A DATA SET: the path of each of the eight inputs. Default = the files of the Archives folder; the data files window
    // builds one from the user's files. mainProgram.LoadFiltered reads it
    public class DataFiles
    {
        public static readonly IReadOnlyList<DataRoleInfo> Roles = new List<DataRoleInfo>
        {
            new DataRoleInfo { Role = DataRole.Airblocks, Title = "Airblocks", Extension = ".are", DefaultFile = "sectors_1608.are",
                               Description = "Polygons: a header 'NbPoints lat lon … name', then its points (arc-minutes)" },
            new DataRoleInfo { Role = DataRole.ElementarySectors, Title = "Elementary sectors", Extension = ".sls", DefaultFile = "sectors_1608.sls",
                               Description = "'SECTOR [+|-] AIRBLOCK bottomFL topFL' per volume" },
            new DataRoleInfo { Role = DataRole.Airspaces, Title = "Airspaces (collapsed sectors)", Extension = ".spc", DefaultFile = "Airspace_1608.spc",
                               Description = "'A;ID;NAME;TYPE;…' followed by its 'S;MEMBER;TYPE' lines" },
            new DataRoleInfo { Role = DataRole.Configurations, Title = "Configurations", Extension = ".cfg", DefaultFile = "Configuration_1608.cfg",
                               Description = "'ACC;CONFIGURATION;SECTOR'" },
            new DataRoleInfo { Role = DataRole.OpeningScheme, Title = "Opening scheme", Extension = ".cos", DefaultFile = "OpeningScheme_1608.cos",
                               Description = "'dd/MM/yyyy;ACC;hh:mm;hh:mm;CONFIG;E/T'" },
            new DataRoleInfo { Role = DataRole.Trajectories, Title = "Traffic (trajectories)", Extension = ".so6", DefaultFile = "20160728Initial_initial.so6",
                               Description = "One segment per line, 20 space-separated fields" },
            new DataRoleInfo { Role = DataRole.Capacities, Title = "Capacities", Extension = ".csv", DefaultFile = "capacity_sector_level.csv",
                               Description = "'Sector;Capacity' per hour (999 = no limit)" },
            new DataRoleInfo { Role = DataRole.Accs, Title = "ACCs of interest", Extension = ".txt", DefaultFile = "accs.txt",
                               Description = "One ACC name per line" },
        };

        public static DataRoleInfo Info(DataRole role) => Roles.First(r => r.Role == role);

        public static string DefaultPath(DataRole role) => Path.Combine(AppContext.BaseDirectory, "Archives", Info(role).DefaultFile);

        private readonly Dictionary<DataRole, string> _paths = new Dictionary<DataRole, string>();
        private readonly Dictionary<DataRole, string> _names = new Dictionary<DataRole, string>();

        public string this[DataRole role]
        {
            get => _paths.TryGetValue(role, out string p) ? p : null;
            set => _paths[role] = value;
        }

        // A copy (UserDataFolder) with the name of the file the user chose, for the exports and the window
        public void Set(DataRole role, string path, string originalName)
        {
            _paths[role] = path;
            _names[role] = originalName;
        }

        public string SourceName(DataRole role) => _names.TryGetValue(role, out string n) ? n : Path.GetFileName(this[role]);

        public bool IsDefault => Roles.All(r => string.Equals(this[r.Role], DefaultPath(r.Role), StringComparison.OrdinalIgnoreCase));

        public static DataFiles Default()
        {
            var files = new DataFiles();
            foreach (DataRoleInfo r in Roles)
                files[r.Role] = DefaultPath(r.Role);
            return files;
        }

        // Role of a file from its extension, null if none
        public static DataRole? RoleOf(string path)
        {
            string ext = Path.GetExtension(path);
            DataRoleInfo info = Roles.FirstOrDefault(r => string.Equals(r.Extension, ext, StringComparison.OrdinalIgnoreCase));
            return info?.Role;
        }

        // ACC names, one per line (blank lines ignored)
        public static HashSet<string> ReadAccs(string path)
        {
            if (!File.Exists(path))
                throw new DataFileException(path, "the file does not exist");
            var accs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int lineNumber = 0;
            foreach (string raw in File.ReadLines(path))
            {
                lineNumber++;
                string name = raw.Trim();
                if (name.Length == 0)
                    continue;
                if (name.Any(c => char.IsWhiteSpace(c) || c == ';' || c == ','))
                    throw new DataFileException(path, lineNumber, raw, "one ACC name per line, without spaces or separators (e.g. LECMCTAN)");
                accs.Add(name);
            }
            if (accs.Count == 0)
                throw new DataFileException(path, "no ACC name found: write one per line");
            return accs;
        }

        // CHECK of one file on its own, with its reader: a summary of what it holds, or DataFileException (file, line, what
        // is wrong). Cross-file problems (e.g. ACCs without configurations) are found by the load
        public static string Check(DataRole role, string path)
        {
            switch (role)
            {
                case DataRole.Airblocks:
                    return $"{new Reader_Airblock().ReadFromFile(path).Length:N0} airblocks";
                case DataRole.ElementarySectors:
                    return $"{Read_ElementarySectors.LoadFromFile(path).Count:N0} elementary sectors";
                case DataRole.Airspaces:
                    SpcFile spc = new SpcReader().Read(path);
                    return $"{spc.Airspaces.Count:N0} airspaces, {spc.Airspaces.Count(a => a.Type == "CS"):N0} collapsed sectors";
                case DataRole.Configurations:
                    var configs = new ConfigurationReader().Read(path, new List<ElementarySector>(), new List<Airspace>()).Configurations;
                    return $"{configs.Count:N0} configurations of {configs.Select(c => c.AccName).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} ACCs";
                case DataRole.OpeningScheme:
                    OpeningScheme cos = Read_OpeningScheme.ReadFile(path);
                    return $"{cos.Records.Count:N0} records, {cos.Records.Select(r => r.AccName).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} ACCs, " +
                           $"{cos.FirstStart:dd/MM/yyyy} - {cos.LastEnd.AddMinutes(-1):dd/MM/yyyy}";
                case DataRole.Trajectories:
                    Trajectory[] flights = new Reader_So6().ReadFromFile(path);
                    DateTime first = flights.Min(f => f.Start), last = flights.Max(f => f.End);
                    return $"{flights.Length:N0} flights, {first:dd/MM HH:mm} - {last:dd/MM HH:mm}";
                case DataRole.Capacities:
                    return $"{new CapacityReader().Read(path).Capacities.Count:N0} sector capacities";
                case DataRole.Accs:
                    HashSet<string> accs = ReadAccs(path);
                    return $"{accs.Count} ACCs: {string.Join(", ", accs.Take(6))}{(accs.Count > 6 ? ", …" : "")}";
            }
            throw new ArgumentException("Unknown role " + role);
        }
    }
}
