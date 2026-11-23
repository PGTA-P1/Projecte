using System;
using System.Collections.Generic;

namespace Projecte1_WPF_.Classes
{
    // Lat/lon rectangle in decimal degrees. Used to keep only the trajectories that can cross the loaded airspace
    public class LatLonBox
    {
        public double MinLat { get; set; }
        public double MaxLat { get; set; }
        public double MinLon { get; set; }
        public double MaxLon { get; set; }

        // Box of every vertex of the airblocks, grown by margin (degrees) on each side. null if there are no vertices
        public static LatLonBox FromAirblocks(IEnumerable<Airblock> airblocks, double margin)
        {
            var box = new LatLonBox { MinLat = double.MaxValue, MaxLat = double.MinValue, MinLon = double.MaxValue, MaxLon = double.MinValue };
            foreach (Airblock a in airblocks)
            {
                foreach (var v in a.Vertices)
                {
                    box.MinLat = Math.Min(box.MinLat, v.Latitude);
                    box.MaxLat = Math.Max(box.MaxLat, v.Latitude);
                    box.MinLon = Math.Min(box.MinLon, v.Longitude);
                    box.MaxLon = Math.Max(box.MaxLon, v.Longitude);
                }
            }
            if (box.MinLat > box.MaxLat)
                return null;

            box.MinLat -= margin; box.MaxLat += margin;
            box.MinLon -= margin; box.MaxLon += margin;
            return box;
        }

        // true if the box of the segment overlaps this box. Conservative: it can say true for a segment that only
        // passes near a corner, but never false for a segment that really enters
        public bool Intersects(double lat1, double lon1, double lat2, double lon2)
        {
            return Math.Max(lat1, lat2) >= MinLat && Math.Min(lat1, lat2) <= MaxLat &&
                   Math.Max(lon1, lon2) >= MinLon && Math.Min(lon1, lon2) <= MaxLon;
        }
    }
}
