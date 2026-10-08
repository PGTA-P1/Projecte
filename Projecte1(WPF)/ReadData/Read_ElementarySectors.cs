using System;
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
        // Line format: SECTOR_NAME [+|-] VOLUME_NAME bottomFL topFL
        // The lines of one sector are consecutive in the file, so they are grouped without any lookup table.
        public static List<ElementarySector> LoadFromFile(string filePath)
        {
            var sectors = new List<ElementarySector>();

            foreach (string rawLine in File.ReadAllLines(filePath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] t = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 4)
                    continue;

                int n = t.Length;
                int topLevel = int.Parse(t[n - 1], CultureInfo.InvariantCulture);
                int bottomLevel = int.Parse(t[n - 2], CultureInfo.InvariantCulture);
                string volumeName = t[n - 3];

                // Optional sign just before the volume name
                char sign = '+';
                int nameEnd = n - 4;
                if (nameEnd >= 0 && (t[nameEnd] == "+" || t[nameEnd] == "-"))
                {
                    sign = t[nameEnd][0];
                    nameEnd--;
                }

                if (nameEnd < 0)
                    continue; // invalid line

                string sectorName = string.Join(" ", t.Take(nameEnd + 1));

                // Same name as the previous line -> same sector; otherwise start a new one
                ElementarySector current = sectors.Count > 0 ? sectors[sectors.Count - 1] : null;
                if (current == null || current.Name != sectorName)
                {
                    current = new ElementarySector(sectorName);
                    sectors.Add(current);
                }

                current.Volumes.Add(new SectorVolume(sign, volumeName, bottomLevel, topLevel));
            }

            return sectors;
        }
    }
}