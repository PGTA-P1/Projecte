using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{
    internal class TrajectorySegment
    {
        public string PointFrom { get; set; } // 0 = climb, 1 = descent, 2 = cruise
        public string PointTo { get; set; }

        public DateTime BeginDateTime { get; set; }
        public DateTime EndDateTime { get; set; }

        public int FLBegin { get; set; }
        public int FLEnd { get; set; }
        public int Status {  get; set; }

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
