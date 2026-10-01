using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{
    internal class Trajectory
    {
        public long FlightIdentifier { get; set; }
        public string CallSign { get; set; }
        public string Origin { get; set; }        // ICAO
        public string Destination { get; set; }   // ICAO
        public string AircraftType { get; set; }

        public List<TrajectorySegment> Segments { get; set; } = new List<TrajectorySegment>();
    }
}
