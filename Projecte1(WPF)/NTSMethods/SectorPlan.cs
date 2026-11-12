using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using NetTopologySuite.Algorithm.Locate;
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
        // Flight level of each member border (same index): where the two elementary sectors meet, for the 3D lines
        public List<int> MemberBorderLevels { get; } = new List<int>();
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
            var memberBands = new List<List<(int Top, IndexedPointInAreaLocator Locator)>>();
            foreach (PlanMember m in members.Where(m => m != null))
            {
                var bands = m.Bands.Where(b => b.Bottom < flTo && b.Top > flFrom).ToList();
                List<Geometry> inRange = bands.Select(b => b.Shape).ToList();
                if (inRange.Count == 0)
                    continue;
                bandShapes.Add(inRange);
                memberShapes.Add(inRange.Count == m.Bands.Count ? m.Shape : Union(inRange));
                memberBands.Add(bands.Select(b => (Math.Min(b.Top, flTo), new IndexedPointInAreaLocator(b.Shape))).ToList());
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

                // Member borders, grouped by the level where the members meet (BorderLevel) so each 3D line has one height
                if (memberShapes.Count > 1)
                    foreach (var level in InnerPieces(memberShapes.Select(m => m.Boundary), drawn).GroupBy(s => BorderLevel(s, memberBands)))
                    {
                        AddLines(plan.MemberBorders, level.ToList());
                        while (plan.MemberBorderLevels.Count < plan.MemberBorders.Count)
                            plan.MemberBorderLevels.Add(level.Key);
                    }

                // Level steps: band outlines of the members with more than one band
                AddLines(plan.LevelSteps, InnerPieces(bandShapes.Where(s => s.Count > 1).SelectMany(s => s.Select(b => b.Boundary)), drawn));
            }
            catch (Exception ex)
            {
                plan.MemberBorders.Clear();
                plan.MemberBorderLevels.Clear();
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

        // Each side of a border piece is tested this far from its middle (degrees, ~400 m): more than the simplification
        // tolerance, so a member whose simplified edge is slightly off the piece is still on the right side
        private const double SIDE_DEG = 0.004;

        // Flight level where the members meet along a border piece. A member present on only one side of the piece ends
        // there; its top on that side is the top of the wall between side-by-side members, or the face between stacked
        // ones (a lower member under a higher one: e.g. LECPOXX FL000-145 under LECPGXX FL145-245 meet at FL145). With
        // several such members the lowest top wins (the shared wall ends there). Tops are already cut to the levels shown
        private static int BorderLevel(LineSegment s, List<List<(int Top, IndexedPointInAreaLocator Locator)>> members)
        {
            Coordinate mid = s.MidPoint;
            double length = s.Length;
            double nx = length > 0 ? -(s.P1.Y - s.P0.Y) / length * SIDE_DEG : 0;
            double ny = length > 0 ? (s.P1.X - s.P0.X) / length * SIDE_DEG : 0;
            var left = new Coordinate(mid.X + nx, mid.Y + ny);
            var right = new Coordinate(mid.X - nx, mid.Y - ny);

            int level = int.MaxValue, highest = 0;
            foreach (var bands in members)
            {
                int topLeft = TopAt(bands, left), topRight = TopAt(bands, right);
                highest = Math.Max(highest, Math.Max(topLeft, topRight));
                if ((topLeft < 0) != (topRight < 0))
                    level = Math.Min(level, Math.Max(topLeft, topRight));
            }
            return level == int.MaxValue ? highest : level; // no member ends here (odd shape): the highest top
        }

        // Highest top of the member's bands containing p, -1 if none
        private static int TopAt(List<(int Top, IndexedPointInAreaLocator Locator)> bands, Coordinate p)
        {
            int top = -1;
            foreach (var b in bands)
                if (b.Top > top && b.Locator.Locate(p) != Location.Exterior)
                    top = b.Top;
            return top;
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
