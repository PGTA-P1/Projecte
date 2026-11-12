using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Projecte1_WPF_.ReadData
{
    /// Reads the .cfg (columns: ACC ; CONFIG ; SECTOR, a configuration is identified by ACC + CONFIG) and, while reading, sorts each sector name into
    /// the elementary list or the collapsed list of its configuration. The two lists are independent.
    public class ConfigurationReader
    {
        private const string FORMAT = "'ACC;CONFIGURATION;SECTOR'";

        // accs: if not null, only the lines of those ACCs are read (null = all the ACCs).
        // Throws DataFileException (file, line, what is wrong) on the first line that does not follow the format
        public ConfigurationList Read(string filePath, List<ElementarySector> elementary, List<Airspace> collapsed, HashSet<string> accs = null)
        {
            if (!File.Exists(filePath))
                throw new DataFileException(filePath, "the file does not exist");

            // Name -> sector, so each of the ~35,000 lines is resolved without scanning the lists
            var elementaryByName = new Dictionary<string, ElementarySector>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < elementary.Count; i++)
                elementaryByName[elementary[i].Name] = elementary[i];

            var collapsedByName = new Dictionary<string, Airspace>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < collapsed.Count; i++)
                collapsedByName[collapsed[i].Id] = collapsed[i];

            var result = new ConfigurationList();
            var byKey = new Dictionary<string, Configuration>(StringComparer.OrdinalIgnoreCase);
            int lineNumber = 0, found = 0;

            foreach (var rawLine in File.ReadLines(filePath))
            {
                lineNumber++;
                var line = rawLine.Trim();

                // Omitir lineas vacias o la cabecera de metadatos (que empieza por '#')
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                var p = new LineParser(filePath, lineNumber, rawLine);
                var parts = p.Fields(';', 3, FORMAT);
                string accName = parts[0].Trim();
                string configName = parts[1].Trim();
                string sectorName = parts[2].Trim();
                if (accName.Length == 0 || configName.Length == 0 || sectorName.Length == 0)
                    throw p.Error($"the ACC, the configuration and the sector cannot be empty: expected {FORMAT}");
                found++;

                if (accs != null && !accs.Contains(accName))
                    continue; // ACC not requested

                // One Configuration per (ACC, name); its lines are not guaranteed to be consecutive
                string key = accName + ";" + configName;
                if (!byKey.TryGetValue(key, out Configuration config))
                {
                    config = new Configuration { AccName = accName, ConfigurationName = configName };
                    byKey[key] = config;
                    result.Configurations.Add(config);
                }

                if (elementaryByName.TryGetValue(sectorName, out ElementarySector es))
                    config.Elementary.Add(es);
                else if (collapsedByName.TryGetValue(sectorName, out Airspace cs))
                    config.Collapsed.Add(cs);
                else
                {
                    Debug.WriteLine($"[ConfigurationReader] '{accName};{configName}': sector '{sectorName}' not found.");
                    config.NotFound.Add(sectorName);
                }
            }

            if (found == 0)
                throw new DataFileException(filePath, $"no configuration line found: expected lines {FORMAT}");
            return result;
        }
    }
}
