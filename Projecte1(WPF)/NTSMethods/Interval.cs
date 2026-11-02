using System;
using System.Collections.Generic;

namespace Projecte1_WPF_.NTS
{
    // Time interval [Start, End] that a flight spends inside a volume or sector
    public struct Interval
    {
        public DateTime Start;
        public DateTime End;

        public Interval(DateTime start, DateTime end) { Start = start; End = end; }

        public double Seconds { get { return (End - Start).TotalSeconds; } }

        public override string ToString() { return $"{Start:HH:mm:ss}-{End:HH:mm:ss} ({Seconds:F0} s)"; }

        // Two intervals closer than this are the same stay (consecutive segments share their end point)
        public static readonly TimeSpan TOUCH = TimeSpan.FromMilliseconds(1);

        // Sorts the intervals and joins the ones that overlap or whose gap is < minGap (a gap >= minGap keeps them apart).
        // Used with TOUCH to join consecutive segments, and with the reentry time for reentries
        public static List<Interval> Merge(List<Interval> intervals, TimeSpan minGap)
        {
            var sorted = new List<Interval>(intervals);
            sorted.Sort((x, y) => x.Start.CompareTo(y.Start));

            var merged = new List<Interval>();
            foreach (Interval i in sorted)
            {
                // Spec: a gap >= reentry time is a NEW entry, so only a gap strictly below minGap joins
                if (merged.Count > 0 && (i.Start <= merged[merged.Count - 1].End || i.Start - merged[merged.Count - 1].End < minGap))
                {
                    Interval last = merged[merged.Count - 1];
                    if (i.End > last.End) last.End = i.End;
                    merged[merged.Count - 1] = last;
                }
                else
                    merged.Add(i);
            }
            return merged;
        }
    }
}
