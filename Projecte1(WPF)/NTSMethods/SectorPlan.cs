using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Quadtree;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Simplify;

namespace Projecte1_WPF_.NTS
{
    // PLAN VIEW (2D map) of one sector of a configuration, from the same fused band shapes as the 3D prisms:
    //   Outline        footprint of the whole sector (all its bands, all its members): drawn as a solid line
    //   MemberBorders  collapsed sector only: borders between the elementary sectors it is made of, inside the footprint
    //                  (dashed)
    //   LevelSteps     inside one elementary sector, where its floor or ceiling changes between bands (dotted)
    // A line already drawn by a stronger style is not repeated (outline > member border > level step), nor a border two
    // members share, and leftovers shorter than MIN_LINE_DEG are dropped, so the map does not fill with lines
    public class SectorPlan
    {
        public string Name { get; set; }
        public List<List<(double Latitude, double Longitude)>> Outline { get; } = new List<List<(double Latitude, double Longitude)>>();
        public List<List<(double Latitude, double Longitude)>> MemberBorders { get; } = new List<List<(double Latitude, double Longitude)>>();
        public List<List<(double Latitude, double Longitude)>> LevelSteps { get; } = new List<List<(double Latitude, double Longitude)>>();
    }

    // One elementary sector ready for the plans: its band shapes simplified, with their flight levels, and their union.
    // Built once per elementary sector (Prepare) and shared by every plan it is a member of
    public class PlanMember
    {
        public List<(int Bottom, int Top, Geometry Shape)> Bands;
        public Geometry Shape;
    }

    public static class SectorPlanBuilder
    {
        // A piece within this distance of a line already drawn is the same border (degrees, ~500 m). Larger than the
        // simplification tolerance, since simplified neighbours no longer share their edges exactly
        private const double SNAP_DEG = 0.005;

        // Borders are tested in pieces of at most this length (degrees, ~2 km): a long edge can be partly on the outline
        private const double PIECE_DEG = 0.02;

        // Shorter leftover lines are dropped (degrees, ~3 km)
        private const double MIN_LINE_DEG = 0.03;

        private static readonly GeometryFactory Factory = new GeometryFactory();

        // FL999 in the files = no upper limit
        public const int NO_UPPER_LIMIT = 999;

        // Exact band shapes of an elementary sector (AirblockMerger.MergeGeometries) simplified like the 3D outlines (the
        // raw airblock outlines have many vertices), and their union. null if it has no shape
        public static PlanMember Prepare(List<BandGeometry> bands, double tolerance = AirblockMerger.DEFAULT_SIMPLIFY_DEG)
        {
            if (bands.Count == 0)
                return null;
            var simple = bands.Select(b => (b.BottomLevel, b.TopLevel, TopologyPreservingSimplifier.Simplify(b.Shape, tolerance))).ToList();
            return new PlanMember { Bands = simple, Shape = Union(simple.Select(b => b.Item3).ToList()) };
        }

        // members: the leaf elementary sectors of the sector (Prepare); an elementary sector is its own only member.
        // flFrom/flTo: flight levels shown (display filter only: the entries and demand always use the whole airspace);
        // only the bands that reach into [flFrom, flTo) count. An empty plan (no outline) = entirely outside the range
        public static SectorPlan Build(string name, List<PlanMember> members, int flFrom = 0, int flTo = NO_UPPER_LIMIT)
        {
            var plan = new SectorPlan { Name = name };
            var bandShapes = new List<List<Geometry>>();
            var memberShapes = new List<Geometry>();
            foreach (PlanMember m in members.Where(m => m != null))
            {
                List<Geometry> inRange = m.Bands.Where(b => b.Bottom < flTo && b.Top > flFrom).Select(b => b.Shape).ToList();
                if (inRange.Count == 0)
                    continue;
                bandShapes.Add(inRange);
                memberShapes.Add(inRange.Count == m.Bands.Count ? m.Shape : Union(inRange));
            }
            if (memberShapes.Count == 0)
                return plan;
            Geometry footprint = Union(memberShapes);

            // Footprint; outer rings only, like the 3D outlines
            foreach (Geometry g in PolygonExtracter.GetPolygons(footprint))
            {
                Polygon p = (Polygon)g;
                if (!p.IsEmpty)
                    plan.Outline.Add(p.ExteriorRing.Coordinates.Select(c => (c.Y, c.X)).ToList());
            }

            // Inner lines. Display only: if NTS fails on some odd shape the sector keeps its outline, without inner lines
            try
            {
                var drawn = new SegmentIndex();               // lines already drawn, strongest style first
                drawn.AddLines(footprint.Boundary);

                if (memberShapes.Count > 1)
                    AddLines(plan.MemberBorders, InnerPieces(memberShapes.Select(m => m.Boundary), drawn));

                // Level steps: band outlines of the members with more than one band
                AddLines(plan.LevelSteps, InnerPieces(bandShapes.Where(s => s.Count > 1).SelectMany(s => s.Select(b => b.Boundary)), drawn));
            }
            catch (Exception ex)
            {
                plan.MemberBorders.Clear();
                plan.LevelSteps.Clear();
                Debug.WriteLine($"[SectorPlan] '{name}': inner lines skipped: {ex.Message}");
            }
            return plan;
        }

        // Pieces of the boundaries that are not on a line already drawn; each kept piece is added to drawn, so a border two
        // members share comes out once
        private static List<LineSegment> InnerPieces(IEnumerable<Geometry> boundaries, SegmentIndex drawn)
        {
            var kept = new List<LineSegment>();
            foreach (Geometry boundary in boundaries)
                foreach (Geometry line in LineStringExtracter.GetLines(boundary))
                {
                    Coordinate[] c = line.Coordinates;
                    for (int i = 0; i + 1 < c.Length; i++)
                    {
                        int pieces = Math.Max(1, (int)Math.Ceiling(c[i].Distance(c[i + 1]) / PIECE_DEG));
                        for (int k = 0; k < pieces; k++)
                        {
                            var piece = new LineSegment(Lerp(c[i], c[i + 1], (double)k / pieces), Lerp(c[i], c[i + 1], (double)(k + 1) / pieces));
                            if (drawn.Near(piece.MidPoint))
                                continue;
                            kept.Add(piece);
                            drawn.Add(piece);
                        }
                    }
                }
            return kept;
        }

        // Kept pieces joined into the longest possible lines, short ones dropped
        private static void AddLines(List<List<(double Latitude, double Longitude)>> target, List<LineSegment> pieces)
        {
            if (pieces.Count == 0)
                return;
            var merger = new LineMerger();
            merger.Add(pieces.Select(s => (Geometry)Factory.CreateLineString(new[] { s.P0, s.P1 })).ToList());
            foreach (Geometry line in merger.GetMergedLineStrings())
                if (line.Length >= MIN_LINE_DEG)
                    target.Add(DouglasPeuckerSimplifier.Simplify(line, AirblockMerger.DEFAULT_SIMPLIFY_DEG)
                                                     .Coordinates.Select(c => (c.Y, c.X)).ToList());
        }

        // Union with snap rounding at 1e-6 degrees (about 0.1 m; display only). The classic overlay throws "non-noded
        // intersection" on some simplified shapes, and OverlayNGRobust only gets there after a failed floating attempt
        // (an exception inside NTS each time, 23 per load, slow under a debugger); snap rounding never fails
        private static readonly PrecisionModel Snap = new PrecisionModel(1e6);

        private static Geometry Union(List<Geometry> geometries)
        {
            return geometries.Count == 1 ? geometries[0] : UnaryUnionNG.Union(Factory.BuildGeometry(geometries), Snap);
        }

        private static Coordinate Lerp(Coordinate a, Coordinate b, double f)
        {
            return new Coordinate(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f);
        }

        // Segments with a quick "is this point within SNAP_DEG of one of them" test (quadtree: insert while querying)
        private sealed class SegmentIndex
        {
            private readonly Quadtree<LineSegment> _tree = new Quadtree<LineSegment>();

            public void Add(LineSegment s) => _tree.Insert(new Envelope(s.P0, s.P1), s);

            public void AddLines(Geometry g)
            {
                foreach (Geometry line in LineStringExtracter.GetLines(g))
                {
                    Coordinate[] c = line.Coordinates;
                    for (int i = 0; i + 1 < c.Length; i++)
                        Add(new LineSegment(c[i], c[i + 1]));
                }
            }

            public bool Near(Coordinate p)
            {
                var box = new Envelope(p);
                box.ExpandBy(SNAP_DEG);
                foreach (LineSegment s in _tree.Query(box))
                    if (s.Distance(p) < SNAP_DEG)
                        return true;
                return false;
            }
        }
    }
}
