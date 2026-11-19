using System;
﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ReadData
{
    public class Read_ElementarySectors
    {
        private const string FORMAT = "'SECTOR [+|-] AIRBLOCK bottomFL topFL'";

        // Line format: SECTOR_NAME [+|-] VOLUME_NAME bottomFL topFL
        // The lines of one sector are consecutive in the file, so they are grouped without any lookup table.
        // keep: if not null, only sectors whose name is in the set are stored (null = load everything).
        // Throws DataFileException (file, line, what is wrong) on the first line that does not follow the format.
        // skipped: if not null (skip mode), an elementary sector with a bad line is left out whole (a sector without one
        // of its volumes would have another shape) and noted there
        public static List<ElementarySector> LoadFromFile(string filePath, HashSet<string> keep = null, List<string> skipped = null)
        {
            var sectors = new List<ElementarySector>();
            var bad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int lineNumber = 0, found = 0;

            foreach (string rawLine in File.ReadLines(filePath))
            {
                lineNumber++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                var p = new LineParser(filePath, lineNumber, rawLine);
                try
                {
                    string[] t = p.Fields(null, 4, FORMAT);

                    int n = t.Length;
                    int topLevel = p.Int(t[n - 1], "the top flight level (last field)");
                    int bottomLevel = p.Int(t[n - 2], "the bottom flight level (one before last)");
                    if (bottomLevel > topLevel)
                        throw p.Error($"the bottom level FL{bottomLevel} is above the top level FL{topLevel}");
                    if (bottomLevel < 0)
                        throw p.Error($"the bottom flight level cannot be negative, found {bottomLevel}");
                    string volumeName = t[n - 3];

                    // Optional sign just before the volume name
                    char sign = '+';
                    int nameEnd = n - 4;
                    if (nameEnd >= 0 && (t[nameEnd] == "+" || t[nameEnd] == "-"))
                    {
                        sign = t[nameEnd][0];
                        nameEnd--;
                    }
                    // Another symbol where the sign goes (e.g. '*') used to become part of the sector name
                    else if (nameEnd >= 0 && t[nameEnd].Length == 1 && !char.IsLetterOrDigit(t[nameEnd][0]))
                        throw p.Error($"the sign before the airblock must be '+' or '-', found \"{t[nameEnd]}\"");

                    if (nameEnd < 0)
                        throw p.Error($"no sector name before the sign: expected {FORMAT}");

                    string sectorName = string.Join(" ", t.Take(nameEnd + 1));
                    found++;

                    if (keep != null && !keep.Contains(sectorName))
                        continue; // not needed

                    // Same name as the previous line -> same sector; otherwise start a new one
                    ElementarySector current = sectors.Count > 0 ? sectors[sectors.Count - 1] : null;
                    if (current == null || current.Name != sectorName)
                    {
                        current = new ElementarySector(sectorName);
                        sectors.Add(current);
                    }

                    current.Volumes.Add(new SectorVolume(sign, volumeName, bottomLevel, topLevel));
                }
                catch (DataFileException ex) when (skipped != null)
                {
                    // The sector is the first word of the line
                    string name = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0];
                    bad.Add(name);
                    found++;
                    Skips.Add(skipped, ex, $"elementary sector {name} skipped");
                }
            }

            sectors.RemoveAll(s => bad.Contains(s.Name));
            if (found == 0)
                throw new DataFileException(filePath, "no elementary sector found: is this an .sls file?");
            if (sectors.Count == 0 && keep == null)
                throw new DataFileException(filePath, "no elementary sector left after skipping the bad ones");
            return sectors;
        }
    }
}
