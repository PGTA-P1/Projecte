using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.NTS
{
    /// <summary>
    /// Airblocks of one elementary sector fused into a single outline, for one level band.
    /// Only the outer boundary of each resulting polygon is kept.
    /// </summary>
    public class MergedAirblocks
    {
        public string Name { get; set; }
        public int BottomLevel { get; set; }   // FL
        public int TopLevel { get; set; }      // FL

        // One closed outer ring per resulting polygon, as (Latitude, Longitude) in decimal degrees
        public List<List<(double Latitude, double Longitude)>> OuterRings { get; } =
            new List<List<(double Latitude, double Longitude)>>();
    }

    public static class AirblockMerger
    {
        // Simplification tolerance in degrees (0.002 deg ~ 220 m). Use 0 to keep every vertex.
        public const double DEFAULT_SIMPLIFY_DEG = 0.002;

        private static readonly GeometryFactory Factory = new GeometryFactory();

        /// <summary>
        /// Fuses the airblocks of an elementary sector. Volumes are grouped by (BottomLevel, TopLevel),
        /// because one outline cannot have different vertical limits. Sign '+' adds a volume, '-' subtracts it.
        /// </summary>
        public static List<MergedAirblocks> Merge(
            ElementarySectors sector,
            IEnumerable<Airblock> airblocks,
            double simplifyToleranceDeg = DEFAULT_SIMPLIFY_DEG)
        {
            var result = new List<MergedAirblocks>();

            foreach (var band in sector.Volumes.GroupBy(v => (v.BottomLevel, v.TopLevel)))
            {
                var plus = new List<Geometry>();
                var minus = new List<Geometry>();

                foreach (var vol in band)
                {
                    // Linear search: simple, and fast enough for the few sectors shown at a time
                    Airblock block = airblocks.FirstOrDefault(a =>
                        string.Equals(a.Name, vol.VolumeName, StringComparison.OrdinalIgnoreCase));

                    Polygon poly = ToNtsPolygon(block);
                    if (poly == null)
                    {
                        Debug.WriteLine($"[AirblockMerger] '{sector.Name}': airblock '{vol.VolumeName}' missing or invalid.");
                        continue;
                    }

                    (vol.Sign == '-' ? minus : plus).Add(poly);
                }

                if (plus.Count == 0)
                    continue;

                Geometry merged = UnaryUnionOp.Union(plus);
                if (minus.Count > 0)
                    merged = merged.Difference(UnaryUnionOp.Union(minus));

                // Drop vertices that do not change the shape noticeably
                if (simplifyToleranceDeg > 0 && !merged.IsEmpty)
                    merged = TopologyPreservingSimplifier.Simplify(merged, simplifyToleranceDeg);

                var fused = new MergedAirblocks
                {
                    Name = sector.Name,
                    BottomLevel = band.Key.BottomLevel,
                    TopLevel = band.Key.TopLevel
                };

                // The union can be several disjoint polygons; keep the outer ring of each (holes are ignored)
                foreach (Polygon p in PolygonExtracter.GetPolygons(merged).Cast<Polygon>().Where(p => !p.IsEmpty))
                    fused.OuterRings.Add(p.ExteriorRing.Coordinates.Select(c => (c.Y, c.X)).ToList());

                if (fused.OuterRings.Count > 0)
                    result.Add(fused);
            }

            return result;
        }

        // NTS convention: X = longitude, Y = latitude
        private static Polygon ToNtsPolygon(Airblock airblock)
        {
            if (airblock?.Vertices == null || airblock.Vertices.Count < 3)
                return null;

            var coords = new List<Coordinate>();
            foreach (var v in airblock.Vertices)
            {
                var c = new Coordinate(v.Longitude, v.Latitude);
                if (coords.Count == 0 || !coords[coords.Count - 1].Equals2D(c))
                    coords.Add(c);
            }

            if (!coords[0].Equals2D(coords[coords.Count - 1]))
                coords.Add(coords[0].Copy());   // close the ring

            if (coords.Count < 4)
                return null;

            Polygon poly = Factory.CreatePolygon(coords.ToArray());

            // A self-intersecting ring would make the union throw; Buffer(0) repairs most cases
            if (!poly.IsValid)
                poly = PolygonExtracter.GetPolygons(poly.Buffer(0)).Cast<Polygon>()
                                       .OrderByDescending(p => p.Area).FirstOrDefault();

            return poly == null || poly.IsEmpty ? null : poly;
        }
    }
}