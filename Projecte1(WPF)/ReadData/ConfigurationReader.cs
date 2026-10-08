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
        public ConfigurationList Read(string filePath, List<ElementarySector> elementary, List<Airspace> collapsed)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"No se encontro el archivo: {filePath}");

            // Name -> sector, so each of the ~35,000 lines is resolved without scanning the lists
            var elementaryByName = new Dictionary<string, ElementarySector>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < elementary.Count; i++)
                elementaryByName[elementary[i].Name] = elementary[i];

            var collapsedByName = new Dictionary<string, Airspace>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < collapsed.Count; i++)
                collapsedByName[collapsed[i].Id] = collapsed[i];

            var result = new ConfigurationList();
            var byKey = new Dictionary<string, Configuration>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();

                // Omitir lineas vacias o la cabecera de metadatos (que empieza por '#')
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                var parts = line.Split(';');
                if (parts.Length < 3)
                    continue;

                string accName = parts[0].Trim();
                string configName = parts[1].Trim();
                string sectorName = parts[2].Trim();

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

            return result;
        }
    }
}