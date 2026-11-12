using Projecte1_WPF_.Classes;
using System;
using System.Collections.Generic;
using System.IO;

namespace Projecte1_WPF_.ReadData
{
    /// Clase encargada de procesar archivos CSV delimitados por punto y coma (;) para capacidades.
    public class CapacityReader
    {
        private const string FORMAT = "'Sector;Capacity'";

        // keep: if not null, only the sectors whose name is in the set are stored (null = load everything).
        // Throws DataFileException (file, line, what is wrong) on the first line that does not follow the format
        public CapacityFile Read(string filePath, HashSet<string> keep = null)
        {
            if (!File.Exists(filePath))
                throw new DataFileException(filePath, "the file does not exist");

            var result = new CapacityFile();
            bool isFirstLine = true;
            int lineNumber = 0, found = 0;

            foreach (var rawLine in File.ReadLines(filePath))
            {
                lineNumber++;
                var line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Omitir la primera línea si es la cabecera (Sector;Capacity)
                if (isFirstLine)
                {
                    isFirstLine = false;
                    if (line.StartsWith("Sector", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var p = new LineParser(filePath, lineNumber, rawLine);
                var parts = p.Fields(';', 2, FORMAT);
                string sector = parts[0].Trim();
                if (sector.Length == 0)
                    throw p.Error("the sector name (1st field) is empty");
                double capacity = p.Double(parts[1].Trim(), "the hourly capacity (2nd field)");
                if (capacity < 0)
                    throw p.Error($"the hourly capacity cannot be negative, found {parts[1].Trim()}");
                found++;

                if (keep != null && !keep.Contains(sector))
                    continue; // sector not needed

                result.Capacities.Add(new CapacityRecord { SectorName = sector, HourlyCapacity = capacity });
            }

            if (found == 0)
                throw new DataFileException(filePath, $"no capacity line found: expected lines {FORMAT}");
            return result;
        }
    }
}
