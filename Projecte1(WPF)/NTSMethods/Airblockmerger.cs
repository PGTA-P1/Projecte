using System;
using System.Collections.Generic;
using System.Diagnostics;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.NTS
{
    // Outline of one sector (elementary or collapsed) for one level band
    // What the plotter needs from any outline, so elementary and collapsed results stay separate types
    public interface IMergedSector
    {
        string Name { get; }
        int BottomLevel { get; }
        int TopLevel { get; }
        List<List<(double Latitude, double Longitude)>> OuterRings { get; }
    }

    public class MergedAirblocks : IMergedSector
    {
        public string Name { get; set; }
        public int BottomLevel { get; set; }
        public int TopLevel { get; set; }

        // One closed outer ring per resulting polygon, as (Latitude, Longitude) in degrees
        public List<List<(double Latitude, double Longitude)>> OuterRings { get; } =
            new List<List<(double Latitude, double Longitude)>>();
    }

    // Exact (not simplified) shape of a sector for one level band. Used as input of CollapsedSectorMerger
    public class BandGeometry
    {
        public int BottomLevel;
        public int TopLevel;
        public Geometry Shape;
    }

    public static class AirblockMerger
    {
        // Simplification tolerance in degrees (0.002 deg ~ 220 m). 0 keeps every vertex.
        public const double DEFAULT_SIMPLIFY_DEG = 0.002;

        private static readonly GeometryFactory Factory = new GeometryFactory();

        // One result per different (BottomLevel, TopLevel) of the sector, since an outline has a single height range
        public static List<MergedAirblocks> Merge(ElementarySector sector, List<Airblock> airblocks,
                                                  double tolerance = DEFAULT_SIMPLIFY_DEG)
        {
            var result = new List<MergedAirblocks>();
            List<BandGeometry> bands = MergeGeometries(sector, airblocks);

            for (int i = 0; i < bands.Count; i++)
            {
                MergedAirblocks fused = ToMerged(sector.Name, bands[i].BottomLevel, bands[i].TopLevel,
                                                 bands[i].Shape, tolerance);
                if (fused != null)
                    result.Add(fused);
            }

            return result;
        }

        // Airblocks of the sector fused by level band (the '-' volumes are subtracted). No simplification here
        public static List<BandGeometry> MergeGeometries(ElementarySector sector, List<Airblock> airblocks)
        {
            var result = new List<BandGeometry>();
            int n = sector.Volumes.Count;
            bool[] used = new bool[n];

            for (int i = 0; i < n; i++)
            {
                if (used[i])
                    continue;

                int bottom = sector.Volumes[i].BottomLevel;
                int top = sector.Volumes[i].TopLevel;
                var plus = new List<Geometry>();
                var minus = new List<Geometry>();

                for (int j = i; j < n; j++)
                {
                    SectorVolume vol = sector.Volumes[j];
                    if (used[j] || vol.BottomLevel != bottom || vol.TopLevel != top)
                        continue;
                    used[j] = true;

                    Polygon poly = ToPolygon(FindAirblock(airblocks, vol.VolumeName));
                    if (poly == null)
                    {
                        Debug.WriteLine($"[AirblockMerger] '{sector.Name}': airblock '{vol.VolumeName}' missing or invalid.");
                        continue;
                    }

                    if (vol.Sign == '-')
                        minus.Add(poly);
                    else
                        plus.Add(poly);
                }

                if (plus.Count == 0)
                    continue;

                Geometry merged = UnaryUnionOp.Union(plus);
                if (minus.Count > 0)
                    merged = merged.Difference(UnaryUnionOp.Union(minus));

                if (merged.IsEmpty)
                    continue;

                result.Add(new BandGeometry { BottomLevel = bottom, TopLevel = top, Shape = merged });
            }

            return result;
        }

        // Simplifies a shape and keeps only the outer ring of each polygon. Returns null if nothing is left
        public static MergedAirblocks ToMerged(string name, int bottom, int top, Geometry shape, double tolerance)
        {
            if (tolerance > 0 && !shape.IsEmpty)
                shape = TopologyPreservingSimplifier.Simplify(shape, tolerance);

            var fused = new MergedAirblocks { Name = name, BottomLevel = bottom, TopLevel = top };

            // The union can give several separate polygons; only the outer ring of each is kept
            foreach (Geometry g in PolygonExtracter.GetPolygons(shape))
            {
                Polygon p = (Polygon)g;
                if (p.IsEmpty)
                    continue;

                var ring = new List<(double Latitude, double Longitude)>();
                foreach (Coordinate c in p.ExteriorRing.Coordinates)
                    ring.Add((c.Y, c.X));
                fused.OuterRings.Add(ring);
            }

            return fused.OuterRings.Count > 0 ? fused : null;
        }

        private static Airblock FindAirblock(List<Airblock> airblocks, string name)
        {
            for (int i = 0; i < airblocks.Count; i++)
            {
                if (string.Equals(airblocks[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return airblocks[i];
            }
            return null;
        }

        // NTS uses X = longitude, Y = latitude
        private static Polygon ToPolygon(Airblock airblock)
        {
            if (airblock == null || airblock.Vertices.Count < 3)
                return null;

            var coords = new List<Coordinate>();
            foreach (var v in airblock.Vertices)
            {
                var c = new Coordinate(v.Longitude, v.Latitude);
                if (coords.Count == 0 || !coords[coords.Count - 1].Equals2D(c))
                    coords.Add(c);
            }

            if (!coords[0].Equals2D(coords[coords.Count - 1]))
                coords.Add(coords[0].Copy());

            if (coords.Count < 4)
                return null;

            Polygon poly = Factory.CreatePolygon(coords.ToArray());

            // Buffer(0) repairs a self-intersecting ring; keep its biggest piece
            if (!poly.IsValid)
            {
                Polygon biggest = null;
                foreach (Geometry g in PolygonExtracter.GetPolygons(poly.Buffer(0)))
                {
                    if (biggest == null || g.Area > biggest.Area)
                        biggest = (Polygon)g;
                }
                poly = biggest;
            }

            return poly == null || poly.IsEmpty ? null : poly;
        }
    }
}