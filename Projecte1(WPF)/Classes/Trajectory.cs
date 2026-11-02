using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{
    public class Trajectory
    {
        public long FlightIdentifier { get; set; }
        public string CallSign { get; set; }
        public string Origin { get; set; }        // ICAO
        public string Destination { get; set; }   // ICAO
        public string AircraftType { get; set; }

        public List<TrajectorySegment> Segments { get; set; } = new List<TrajectorySegment>();

        public DateTime Start { get { return Segments[0].BeginDateTime; } }
        public DateTime End { get { return Segments[Segments.Count - 1].EndDateTime; } }

        // Position at a time by linear interpolation inside its segment (lat/lon in degrees, FL).
        // false if the flight is not in the air at that time
        public bool TryGetPosition(DateTime time, out double latitude, out double longitude, out double fl)
        {
            latitude = longitude = fl = 0;
            if (Segments.Count == 0 || time < Start || time > End)
                return false;

            // First segment that ends at or after time (segments are consecutive and in time order)
            int lo = 0, hi = Segments.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (Segments[mid].EndDateTime < time) lo = mid + 1;
                else hi = mid;
            }

            TrajectorySegment s = Segments[lo];
            double span = (s.EndDateTime - s.BeginDateTime).TotalSeconds;
            double u = span <= 0 ? 0 : Math.Max(0, Math.Min(1, (time - s.BeginDateTime).TotalSeconds / span));
            latitude = s.LatBegin + u * (s.LatEnd - s.LatBegin);
            longitude = s.LonBegin + u * (s.LonEnd - s.LonBegin);
            fl = s.FLBegin + u * (s.FLEnd - s.FLBegin);
            return true;
        }
    }

    public class TrajectorySegment
    {
        public string PointFrom { get; set; } // 0 = climb, 1 = descent, 2 = cruise
        public string PointTo { get; set; }

        public DateTime BeginDateTime { get; set; }
        public DateTime EndDateTime { get; set; }

        public int FLBegin { get; set; }
        public int FLEnd { get; set; }
        public int Status { get; set; }

        //Coordinates

        public double LatBegin { get; set; }
        public double LonBegin { get; set; }

        public double LatEnd { get; set; }

        public double LonEnd { get; set; }

        public int Sequence { get; set; }
        public double LengthNm { get; set; }
        public int ParityColour { get; set; }
    }
}
