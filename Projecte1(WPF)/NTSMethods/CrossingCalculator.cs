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
    // One VOLUME of an elementary sector (slides "Airblock" / "Elementary sector"): the fused airblocks of one level band,
    // horizontal polygon + altitude limits [FlMin, FlMax)
    public class CrossingVolume
    {
        public string Sector;
        public int FlMin;
        public int FlMax;
        public Geometry Shape;              // NTS: X = longitude, Y = latitude
        public IPreparedGeometry Prepared;  // fast Intersects / Covers tests on Shape
    }

    // Normalised position along one trajectory segment, u in [0, 1] (slides: "u = [0,1]"): u = 0 at the segment start,
    // u = 1 at its end. Entry/Exit are where the segment becomes / stops being inside
    public struct UInterval
    {
        public double Entry;
        public double Exit;

        public UInterval(double entry, double exit) { Entry = entry; Exit = exit; }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "[{0:F3}, {1:F3}]", Entry, Exit);
        }
    }

    // Debug record of one (segment, volume) test with the values of each step of the slides. Cross adds one per candidate
    // volume when a log list is given; Checks > Crossings of sample flights prints them
    public class CrossingStep
    {
        public int Segment;                                        // index in trajectory.Segments
        public string Sector;
        public int FlMin, FlMax;
        public List<UInterval> Horizontal = new List<UInterval>(); // 1. u_h_entry, u_h_exit (one per piece inside)
        public UInterval? Vertical;                                // 2. u_v_entry, u_v_exit (null: never inside the FL band)
        public List<UInterval> Both = new List<UInterval>();       // 3. u_entry, u_exit = horizontal AND vertical
        public List<Interval> Times = new List<Interval>();        // 4. entry/exit times by linear interpolation
    }

    // CROSSING ALGORITHM (slides "Trajectory crossing an airspace" and "Crossing algorithm"), geometry only: the opening
    // scheme is applied later by EntryCalculator. For every segment of the trajectory and every volume it may cross:
    //   1. HORIZONTAL  u_h = part of the segment inside the polygon (NTS intersection, Shapely.intersection in the slides)
    //   2. VERTICAL    u_v = part of the segment whose flight level is inside [FlMin, FlMax)
    //   3. BOTH        u_entry = max(u_h_entry, u_v_entry), u_exit = min(u_h_exit, u_v_exit)  (horizontal AND vertical)
    //   4. LINEAR INTERPOLATION  t = t0 + u (t1 - t0)  -> entry and exit times
    // Entry = when the trajectory first becomes inside, exit = when it stops being inside: the pieces of consecutive
    // segments are joined into one stay per sector.
    public class CrossingCalculator
    {
        private static readonly GeometryFactory Factory = new GeometryFactory();

        private readonly STRtree<CrossingVolume> _index = new STRtree<CrossingVolume>();
        private readonly List<CrossingVolume> _volumes = new List<CrossingVolume>();

        public IReadOnlyList<CrossingVolume> Volumes { get { return _volumes; } }

        // One CrossingVolume per elementary sector and level band (AirblockMerger.MergeGeometries fuses the airblocks and
        // subtracts the '-' volumes), plus a spatial index so each segment is only tested against volumes near it
        public CrossingCalculator(List<ElementarySector> elementary, List<Airblock> airblocks)
        {
            foreach (ElementarySector es in elementary)
            {
                foreach (BandGeometry band in AirblockMerger.MergeGeometries(es, airblocks))
                {
                    var v = new CrossingVolume
                    {
                        Sector = es.Name,
                        FlMin = band.BottomLevel,
                        FlMax = band.TopLevel,
                        Shape = band.Shape,
                        Prepared = PreparedGeometryFactory.Prepare(band.Shape)
                    };
                    _volumes.Add(v);
                    _index.Insert(band.Shape.EnvelopeInternal, v);

                    // A prepared geometry builds its indexes on first use. A line across its box (from outside) builds
                    // them now, so Cross only reads shared data and ComputeEntries can run flights in parallel
                    Envelope box = band.Shape.EnvelopeInternal;
                    v.Prepared.Intersects(Factory.CreateLineString(new[] {
                        new Coordinate(box.MinX - 1, box.MinY - 1), new Coordinate(box.MaxX + 1, box.MaxY + 1) }));
                }
            }
            _index.Build();
        }

        // Elementary sector name -> time intervals (entry, exit) the flight is inside it, sorted and with the pieces of
        // consecutive segments joined. Empty if the flight never enters any loaded elementary sector
        public Dictionary<string, List<Interval>> Cross(Trajectory trajectory, List<CrossingStep> log = null)
        {
            var result = new Dictionary<string, List<Interval>>(StringComparer.OrdinalIgnoreCase);

            for (int k = 0; k < trajectory.Segments.Count; k++)
            {
                TrajectorySegment s = trajectory.Segments[k];
                if (s.EndDateTime <= s.BeginDateTime)
                    continue; // no time inside anything

                // Segment ends (NTS: X = longitude, Y = latitude); only the volumes whose box touches the segment's box
                var a = new Coordinate(s.LonBegin, s.LatBegin);
                var b = new Coordinate(s.LonEnd, s.LatEnd);
                int segmentFlMin = Math.Min(s.FLBegin, s.FLEnd), segmentFlMax = Math.Max(s.FLBegin, s.FLEnd);
                foreach (CrossingVolume v in _index.Query(new Envelope(a, b)))
                {
                    // Shortcut only: a segment whose levels never reach the band gives an empty vertical, so the AND
                    // (step 3) is empty whatever the horizontal says. Skipping it halves the time; the result is the same
                    if (segmentFlMax < v.FlMin || segmentFlMin >= v.FlMax)
                        continue;

                    var step = new CrossingStep { Segment = k, Sector = v.Sector, FlMin = v.FlMin, FlMax = v.FlMax };
                    log?.Add(step);

                    // 1. HORIZONTAL
                    step.Horizontal = Horizontal(a, b, v);
                    if (step.Horizontal.Count == 0)
                        continue;

                    // 2. VERTICAL
                    step.Vertical = Vertical(s.FLBegin, s.FLEnd, v.FlMin, v.FlMax);
                    if (step.Vertical == null)
                        continue;

                    foreach (UInterval h in step.Horizontal)
                    {
                        // 3. BOTH (horizontal AND vertical)
                        UInterval? both = Both(h, step.Vertical.Value);
                        if (both == null)
                            continue;
                        step.Both.Add(both.Value);

                        // 4. LINEAR INTERPOLATION
                        var times = new Interval(Interpolate(s, both.Value.Entry), Interpolate(s, both.Value.Exit));
                        step.Times.Add(times);
                        if (!result.TryGetValue(v.Sector, out List<Interval> list))
                        {
                            list = new List<Interval>();
                            result[v.Sector] = list;
                        }
                        list.Add(times);
                    }
                }
            }

            // Entry = first becomes inside, exit = stops being inside: consecutive segments share their end point, so one
            // stay comes out as several touching pieces, joined here
            foreach (string sector in new List<string>(result.Keys))
                result[sector] = Interval.Merge(result[sector], Interval.TOUCH);
            return result;
        }

        // 1. HORIZONTAL: u_h_entry, u_h_exit of every piece of the segment a-b inside the polygon of the volume.
        // NTS intersection of the polygon and the segment (Shapely.intersection in the slides); each piece's two ends are
        // projected back onto the segment: u = ((p - a)·(b - a)) / |b - a|²
        public static List<UInterval> Horizontal(Coordinate a, Coordinate b, CrossingVolume v)
        {
            var result = new List<UInterval>();

            // No horizontal movement (pure climb/descent): inside for the whole segment or not at all
            if (a.Equals2D(b))
            {
                if (v.Prepared.Covers(Factory.CreatePoint(a)))
                    result.Add(new UInterval(0, 1));
                return result;
            }

            // Quick answers first: most candidates are completely outside or completely inside
            LineString segment = Factory.CreateLineString(new[] { a, b });
            if (!v.Prepared.Intersects(segment))
                return result;
            if (v.Prepared.Covers(segment))
            {
                result.Add(new UInterval(0, 1));
                return result;
            }

            // The segment crosses the boundary. A segment that only touches a corner gives a Point, which GetLines ignores
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            foreach (Geometry piece in LineStringExtracter.GetLines(v.Shape.Intersection(segment)))
            {
                Coordinate[] c = piece.Coordinates;
                double p = ((c[0].X - a.X) * dx + (c[0].Y - a.Y) * dy) / len2;
                double q = ((c[c.Length - 1].X - a.X) * dx + (c[c.Length - 1].Y - a.Y) * dy) / len2;
                // Pieces can come back reversed
                result.Add(new UInterval(Math.Max(0, Math.Min(p, q)), Math.Min(1, Math.Max(p, q))));
            }
            return result;
        }

        // 2. VERTICAL: u_v_entry, u_v_exit where FL(u) = FL0 + u (FLf - FL0) is inside [flMin, flMax). null if never.
        // Slides example: FLmin 100, FLmax 300, FL0 80, FLf 200 -> u_v_entry = (100 - 80) / (200 - 80) = 0.167, u_v_exit = 1
        public static UInterval? Vertical(int fl0, int flf, int flMin, int flMax)
        {
            // Level flight: the whole segment or nothing. Half-open band: exactly at FlMax belongs to the band above
            if (fl0 == flf)
                return fl0 >= flMin && fl0 < flMax ? new UInterval(0, 1) : (UInterval?)null;

            // Climb or descent: u where FL(u) = flMin and FL(u) = flMax; between them (clipped to [0, 1]) it is inside
            double d = flf - fl0;
            double atMin = (flMin - fl0) / d, atMax = (flMax - fl0) / d;
            var u = new UInterval(Math.Max(0, Math.Min(atMin, atMax)), Math.Min(1, Math.Max(atMin, atMax)));
            return u.Exit > u.Entry ? u : (UInterval?)null;
        }

        // 3. BOTH: inside horizontally AND vertically. Slides example: u_h [0.5, 1] AND u_v [0.167, 1] -> [0.5, 1]
        public static UInterval? Both(UInterval horizontal, UInterval vertical)
        {
            var u = new UInterval(Math.Max(horizontal.Entry, vertical.Entry), Math.Min(horizontal.Exit, vertical.Exit));
            return u.Exit > u.Entry ? u : (UInterval?)null;
        }

        // 4. LINEAR INTERPOLATION: time at position u of the segment, t = t0 + u (t1 - t0)
        public static DateTime Interpolate(TrajectorySegment s, double u)
        {
            return s.BeginDateTime + TimeSpan.FromTicks((long)(u * (s.EndDateTime - s.BeginDateTime).Ticks));
        }
    }
}
