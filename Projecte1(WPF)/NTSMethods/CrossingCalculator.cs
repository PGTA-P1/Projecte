using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Index.Strtree;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.NTS
{
    // One volume of an elementary sector: fused airblocks of one level band [BottomLevel, TopLevel)
    public class CrossingVolume
    {
        public string Sector;
        public int BottomLevel;
        public int TopLevel;
        public Geometry Shape;              // NTS: X = longitude, Y = latitude
        public IPreparedGeometry Prepared;  // fast Intersects / Covers tests on Shape
    }

    // Debug record of one (segment, candidate volume) test. Cross adds one per candidate when a log list is given;
    // Checks > Crossings of sample flights (CrossingCheck) prints them, to see what was decided at each step
    public class CrossingStep
    {
        public int Segment;                                  // index in trajectory.Segments
        public string Sector;
        public int BottomLevel, TopLevel;
        public bool VerticalInside;                          // false -> V0/V1 meaningless, horizontal not computed
        public double V0, V1;                                // vertical u-interval
        public List<(double U0, double U1)> Horizontal = new List<(double U0, double U1)>(); // horizontal u-intervals
        public List<Interval> Result = new List<Interval>(); // time intervals after AND + interpolation
    }

    // GEOMETRIC crossing of the trajectories with the elementary sectors (the opening scheme is NOT used here).
    // For each segment: vertical u-interval AND horizontal u-interval, u in [0,1] along the segment,
    // then u -> time by linear interpolation. Check it with Checks > Crossings of sample flights before running the whole batch.
    public class CrossingCalculator
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        private readonly STRtree<CrossingVolume> _index = new STRtree<CrossingVolume>();
        private readonly List<CrossingVolume> _volumes = new List<CrossingVolume>();

        public IReadOnlyList<CrossingVolume> Volumes { get { return _volumes; } }

        // Builds one CrossingVolume per elementary sector and level band, and the spatial index over them
        public CrossingCalculator(List<ElementarySector> elementary, List<Airblock> airblocks)
        {
            // MergeGeometries: one BandGeometry per (BottomLevel, TopLevel) with the airblocks fused ('-' volumes subtracted)
            foreach (ElementarySector es in elementary)
            {
                foreach (BandGeometry band in AirblockMerger.MergeGeometries(es, airblocks))
                {
                    var v = new CrossingVolume
                    {
                        Sector = es.Name,
                        BottomLevel = band.BottomLevel,
                        TopLevel = band.TopLevel,
                        Shape = band.Shape,
                        Prepared = PreparedGeometryFactory.Prepare(band.Shape)
                    };
                    _volumes.Add(v);
                    _index.Insert(band.Shape.EnvelopeInternal, v);
                }
            }
            _index.Build();
        }

        // Elementary sector name -> time intervals the flight is inside it (sorted, touching intervals merged).
        // Empty if the flight never enters any loaded elementary sector. log: optional, one CrossingStep per test
        public Dictionary<string, List<Interval>> Cross(Trajectory trajectory, List<CrossingStep> log = null)
        {
            var result = new Dictionary<string, List<Interval>>(StringComparer.OrdinalIgnoreCase);

            for (int k = 0; k < trajectory.Segments.Count; k++)
            {
                TrajectorySegment s = trajectory.Segments[k];
                TimeSpan span = s.EndDateTime - s.BeginDateTime;
                if (span <= TimeSpan.Zero)
                    continue; // no time inside anything

                // NTS: X = longitude, Y = latitude
                var a = new Coordinate(s.LonBegin, s.LatBegin);
                var b = new Coordinate(s.LonEnd, s.LatEnd);

                // Only the volumes whose box touches the segment's box
                foreach (CrossingVolume v in _index.Query(new Envelope(a, b)))
                {
                    var step = new CrossingStep { Segment = k, Sector = v.Sector, BottomLevel = v.BottomLevel, TopLevel = v.TopLevel };
                    log?.Add(step);

                    // 1. Vertical first: pure arithmetic, rejects most candidates
                    step.VerticalInside = VerticalInterval(s.FLBegin, s.FLEnd, v.BottomLevel, v.TopLevel, out step.V0, out step.V1);
                    if (!step.VerticalInside)
                        continue;

                    // 2. Horizontal
                    step.Horizontal = HorizontalIntervals(a, b, v);

                    foreach ((double h0, double h1) in step.Horizontal)
                    {
                        // 3. Horizontal AND vertical
                        double u0 = Math.Max(h0, step.V0), u1 = Math.Min(h1, step.V1);
                        if (u1 <= u0)
                            continue;

                        // 4. Linear interpolation u -> time
                        var interval = new Interval(s.BeginDateTime + TimeSpan.FromTicks((long)(u0 * span.Ticks)),
                                                    s.BeginDateTime + TimeSpan.FromTicks((long)(u1 * span.Ticks)));
                        step.Result.Add(interval);
                        if (!result.TryGetValue(v.Sector, out List<Interval> list))
                        {
                            list = new List<Interval>();
                            result[v.Sector] = list;
                        }
                        list.Add(interval);
                    }
                }
            }

            // Consecutive segments share their end point: one stay comes out as several touching pieces
            foreach (string sector in new List<string>(result.Keys))
                result[sector] = Interval.Merge(result[sector], Interval.TOUCH);
            return result;
        }

        // u-interval [u0, u1] where FL(u) = flBegin + u (flEnd - flBegin) is inside [bottom, top). false if empty
        public static bool VerticalInterval(int flBegin, int flEnd, int bottom, int top, out double u0, out double u1)
        {
            u0 = 0; u1 = 1;

            // Level flight: the whole segment or nothing. Half-open band: exactly at the top level belongs to the band above
            if (flBegin == flEnd)
                return flBegin >= bottom && flBegin < top;

            // Climb or descent: u where FL(u) crosses bottom and top; between them (clipped to [0, 1]) it is inside
            double d = flEnd - flBegin;
            double atBottom = (bottom - flBegin) / d, atTop = (top - flBegin) / d;
            u0 = Math.Max(0, Math.Min(atBottom, atTop));
            u1 = Math.Min(1, Math.Max(atBottom, atTop));
            return u1 > u0;
        }

        // u-intervals of the segment a-b (X = lon, Y = lat) that are inside the polygon of the volume
        public static List<(double U0, double U1)> HorizontalIntervals(Coordinate a, Coordinate b, CrossingVolume v)
        {
            var result = new List<(double U0, double U1)>();

            // No horizontal movement (pure climb/descent): inside for the whole segment or not at all
            if (a.Equals2D(b))
            {
                if (v.Prepared.Covers(Factory.CreatePoint(a)))
                    result.Add((0, 1));
                return result;
            }

            // Quick answers first: most candidates are completely outside or completely inside
            LineString line = Factory.CreateLineString(new[] { a, b });
            if (!v.Prepared.Intersects(line))
                return result;
            if (v.Prepared.Covers(line))
            {
                result.Add((0, 1));
                return result;
            }

            // The segment crosses the boundary: pieces of the segment inside the polygon (a corner touch gives a Point,
            // which GetLines ignores), each piece's ends projected back to u
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            foreach (Geometry piece in LineStringExtracter.GetLines(v.Shape.Intersection(line)))
            {
                Coordinate[] c = piece.Coordinates;
                double p = ((c[0].X - a.X) * dx + (c[0].Y - a.Y) * dy) / len2;
                double q = ((c[c.Length - 1].X - a.X) * dx + (c[c.Length - 1].Y - a.Y) * dy) / len2;
                // Pieces can come back reversed
                result.Add((Math.Max(0, Math.Min(p, q)), Math.Min(1, Math.Max(p, q))));
            }
            return result;
        }
    }
}
