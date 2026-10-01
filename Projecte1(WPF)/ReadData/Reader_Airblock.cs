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

        public Airblock[] ReadFromFile(string fileName)
        {
            string[] lines = File.ReadAllLines(fileName);
            var airblockList = new List<Airblock>();
            int i = 0;

            while (i < lines.Length)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }

                string[] header = lines[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (header.Length < 15)
                    throw new FormatException($"Line {i + 1}: expected at least 15 fields, found {header.Length}.");

                double D(int idx) => double.Parse(header[idx], CultureInfo.InvariantCulture);

                var airblock = new Airblock
                {
                    NbPoints = int.Parse(header[0], CultureInfo.InvariantCulture),
                    // Reference point, converted from arc-minutes to decimal degrees
                    Latitude = D(1) / MINUTES_PER_DEGREE,
                    Longitude = D(2) / MINUTES_PER_DEGREE,
                    Flights = D(3),
                    BottomLevel = D(4),
                    TopLevel = D(5),
                    Surface = D(6),
                    SectorNum = D(7),
                    FlightTime = D(8),
                    TrafficDensity = D(9),
                    XMileage = D(10),
                    RteExtens = D(11),
                    ValueOne = D(12),
                    ValueTwo = D(13),
                    Name = string.Join(" ", header.Skip(14))
                };

                i++;

                for (int v = 0; v < airblock.NbPoints; v++)
                {
                    if (i >= lines.Length)
                        throw new FormatException($"Unexpected end of file while reading vertices of '{airblock.Name}'.");

                    string[] point = lines[i].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (point.Length < 2)
                        throw new FormatException($"Line {i + 1}: expected 'lat lon', found '{lines[i]}'.");

                    // Arc-minutes -> decimal degrees. Order in the file is: latitude, longitude.
                    double lat = double.Parse(point[0], CultureInfo.InvariantCulture) / MINUTES_PER_DEGREE;
                    double lon = double.Parse(point[1], CultureInfo.InvariantCulture) / MINUTES_PER_DEGREE;
                    airblock.Vertices.Add((lat, lon));
                    i++;
                }

                airblockList.Add(airblock);
            }

            return airblockList.ToArray();
        }
    }
}