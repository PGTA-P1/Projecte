using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ReadData
{
    internal class Reader_Airblock
    {
        // The .are file stores coordinates in arc-minutes (e.g. 3420.0 = 57.0 degrees).
        private const double MINUTES_PER_DEGREE = 60.0;

        // Header line: NbPoints lat lon flights bottom top surface sectorNum flightTime density xMileage rteExtens v1 v2 NAME
        private const string HEADER = "an airblock header 'NbPoints lat lon … name'";

        // keep: if not null, only airblocks whose name is in the set are stored (null = load everything).
        // Throws DataFileException (file, line, what is wrong) on the first line that does not follow the format.
        // skipped: if not null (skip mode), an airblock with a bad line is left out whole and noted there; reading goes on
        // at the next header line (15 fields or more; a point line has 2)
        public Airblock[] ReadFromFile(string fileName, HashSet<string> keep = null, List<string> skipped = null)
        {
            string[] lines = File.ReadAllLines(fileName);
            var airblockList = new List<Airblock>();
            int found = 0;
            int i = 0;

            while (i < lines.Length)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }

                int headerIndex = i;
                string name = null;
                try
                {
                    var header = new LineParser(fileName, i + 1, lines[i]);
                    string[] h = header.Fields(null, 15, HEADER);
                    name = string.Join(" ", h.Skip(14));
                    int nbPoints = header.Int(h[0], "the number of points (1st field)");
                    if (nbPoints < 3)
                        throw header.Error($"an airblock needs at least 3 points, the header says {nbPoints}");
                    found++;

                    // Not needed: skip the header line and its vertex lines without building anything
                    if (keep != null && !keep.Contains(name))
                    {
                        i += 1 + nbPoints;
                        if (i > lines.Length)
                            throw new DataFileException(fileName, $"the file ends in the middle of airblock '{name}' (line {header.Number:N0} announces {nbPoints} points)");
                        continue;
                    }

                    var p = header;
                    double D(int idx, string what) => p.Double(h[idx], what);

                    var airblock = new Airblock
                    {
                        NbPoints = nbPoints,
                        // Reference point, converted from arc-minutes to decimal degrees
                        Latitude = D(1, "the latitude (arc-minutes)") / MINUTES_PER_DEGREE,
                        Longitude = D(2, "the longitude (arc-minutes)") / MINUTES_PER_DEGREE,
                        Flights = D(3, "field 4 (flights)"),
                        BottomLevel = D(4, "the bottom level"),
                        TopLevel = D(5, "the top level"),
                        Surface = D(6, "field 7 (surface)"),
                        SectorNum = D(7, "field 8 (sector number)"),
                        FlightTime = D(8, "field 9 (flight time)"),
                        TrafficDensity = D(9, "field 10 (traffic density)"),
                        XMileage = D(10, "field 11"),
                        RteExtens = D(11, "field 12"),
                        ValueOne = D(12, "field 13"),
                        ValueTwo = D(13, "field 14"),
                        Name = name
                    };

                    i++;

                    for (int v = 0; v < airblock.NbPoints; v++)
                    {
                        if (i >= lines.Length)
                            throw new DataFileException(fileName, $"the file ends while reading the points of airblock '{airblock.Name}' ({v} of {airblock.NbPoints} read)");

                        var line = new LineParser(fileName, i + 1, lines[i]);
                        string[] point = line.Fields(null, 2, $"a point 'lat lon' of airblock '{airblock.Name}'");
                        if (point.Length >= 15)
                            throw line.Error($"this is the header of another airblock, but airblock '{airblock.Name}' has only {v} of its {airblock.NbPoints} points");

                        // Arc-minutes -> decimal degrees. Order in the file is: latitude, longitude.
                        double lat = line.Double(point[0], "the latitude (arc-minutes)") / MINUTES_PER_DEGREE;
                        double lon = line.Double(point[1], "the longitude (arc-minutes)") / MINUTES_PER_DEGREE;
                        if (Math.Abs(lat) > 90 || Math.Abs(lon) > 180)
                            throw line.Error($"point ({lat:0.###}°, {lon:0.###}°) is outside the Earth: are the coordinates in arc-minutes, latitude first?");
                        airblock.Vertices.Add((lat, lon));
                        i++;
                    }

                    airblockList.Add(airblock);
                }
                catch (DataFileException ex) when (skipped != null)
                {
                    Skips.Add(skipped, ex, name == null ? "airblock skipped" : $"airblock {name} skipped");
                    found++;
                    // On to the next header: the first line after this airblock's header with 15 fields or more
                    i = headerIndex + 1;
                    while (i < lines.Length && lines[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length < 15)
                        i++;
                }
            }

            if (found == 0)
                throw new DataFileException(fileName, "no airblock found: is this an .are file?");
            if (airblockList.Count == 0 && keep == null)
                throw new DataFileException(fileName, "no airblock left after skipping the bad ones");
            return airblockList.ToArray();
        }
    }
}
