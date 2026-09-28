using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Projecte_1.Classes;

namespace Projecte_1.ReadData
{
    internal class Reader_Airblock
    {
        public Airblock[] ReadFromFile(string fileName)
        {
            string[] lines = File.ReadAllLines(fileName);

            List<Airblock> airblockList = new List<Airblock>();

            int i = 0;

            while (i < lines.Length)
            {
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    i++;
                    continue;
                }

                // Read header
                string[] header = lines[i].Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries
                );

                Airblock airblock = new Airblock
                {
                    NbPoints = int.Parse(header[0], CultureInfo.InvariantCulture),
                    Latitude = double.Parse(header[1], CultureInfo.InvariantCulture),
                    Longitude = double.Parse(header[2], CultureInfo.InvariantCulture),
                    Flights = double.Parse(header[3], CultureInfo.InvariantCulture),
                    BottomLevel = double.Parse(header[4], CultureInfo.InvariantCulture),
                    TopLevel = double.Parse(header[5], CultureInfo.InvariantCulture),
                    Surface = double.Parse(header[6], CultureInfo.InvariantCulture),
                    SectorNum = double.Parse(header[7], CultureInfo.InvariantCulture),
                    FlightTime = double.Parse(header[8], CultureInfo.InvariantCulture),
                    TrafficDensity = double.Parse(header[9], CultureInfo.InvariantCulture),
                    XMileage = double.Parse(header[10], CultureInfo.InvariantCulture),
                    RteExtens = double.Parse(header[11], CultureInfo.InvariantCulture),
                    ValueOne = double.Parse(header[12], CultureInfo.InvariantCulture),
                    ValueTwo = double.Parse(header[13], CultureInfo.InvariantCulture),
                    Name = header[14]
                };

                // Move to first vertex
                i++;

                // Read vertices
                for (int v = 0; v < airblock.NbPoints; v++)
                {
                    string[] point = lines[i].Split(
                        new[] { ' ' },
                        StringSplitOptions.RemoveEmptyEntries
                    );

                    double latitude = double.Parse(
                        point[0],
                        CultureInfo.InvariantCulture
                    );

                    double longitude = double.Parse(
                        point[1],
                        CultureInfo.InvariantCulture
                    );

                    airblock.Vertices.Add((latitude, longitude));

                    i++;
                }

                // Add the completed airblock
                airblockList.Add(airblock);
            }

            return airblockList.ToArray();
        }
    }
}
