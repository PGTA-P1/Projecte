using System.Collections.Generic;

namespace Projecte_1.Classes
{
    internal class Airblock
    {
        public int NbPoints { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Flights { get; set; }
        public double BottomLevel { get; set; }
        public double TopLevel { get; set; }
        public double Surface { get; set; }
        public double SectorNum { get; set; }
        public double FlightTime { get; set; }
        public double TrafficDensity { get; set; }
        public double XMileage { get; set; }
        public double RteExtens { get; set; }
        public double ValueOne { get; set; }
        public double ValueTwo { get; set; }
        public string Name { get; set; }

        // Polygon vertices
        public List<(double Latitude, double Longitude)> Vertices { get; set; } = new List<(double Latitude, double Longitude)>();
    }
}
