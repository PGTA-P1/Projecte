using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using Projecte_1.Classes;

namespace Projecte_1.ReadData
{
    internal class Reader_So6
    {
        public Trajectory[] ReadFromFile(string fileName)
        {
            string[] lines = File.ReadAllLines(fileName);

            List<Trajectory> trajectoryList = new List<Trajectory>();

            Trajectory currentTrajectory = null;
            string currentCallSign = null;
            int lastLineNumber = 0;

            // Forces the very first data line to start a trajectory.
            bool reachedDestination = true;

            for (int i = 0; i < lines.Length; i++)
            {
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] field = lines[i].Split(
                    new[] { ' ' },
                    StringSplitOptions.RemoveEmptyEntries
                );

                string callSign = field[9];

                // A new trajectory starts when the callsign changes, or when the
                // previous line already landed on its destination airport.
                if (callSign != currentCallSign || reachedDestination)
                {
                    // The callsign changed but the flight we were building never
                    // reached its destination: report it and stop reading.
                    if (currentTrajectory != null && !reachedDestination)
                    {
                        ReportIncidence(currentTrajectory, lastLineNumber);
                        return trajectoryList.ToArray();
                    }

                    currentTrajectory = new Trajectory
                    {
                        FlightIdentifier = long.Parse(field[16], CultureInfo.InvariantCulture),
                        Origin = field[1],
                        Destination = field[2],
                        AircraftType = field[3],
                        CallSign = callSign
                    };

                    trajectoryList.Add(currentTrajectory);
                    currentCallSign = callSign;
                }

                // Field 1 is "PointFrom_PointTo".
                int splitIndex = field[0].IndexOf('_');
                string pointFrom = field[0].Substring(0, splitIndex);
                string pointTo = field[0].Substring(splitIndex + 1);

                TrajectorySegment segment = new TrajectorySegment
                {
                    PointFrom = pointFrom,
                    PointTo = pointTo,

                    BeginDateTime = ParseDate(field[10]) + ParseTime(field[4]),
                    EndDateTime = ParseDate(field[11]) + ParseTime(field[5]),

                    FLBegin = int.Parse(field[6], CultureInfo.InvariantCulture),
                    FLEnd = int.Parse(field[7], CultureInfo.InvariantCulture),

                    Status = int.Parse(field[8], CultureInfo.InvariantCulture),

                    // so6 stores lat/lon as decimal arc-minutes (degrees*60 + fractional
                    // minutes); divide by 60 to get the decimal degrees we work with.
                    LatBegin = double.Parse(field[12], CultureInfo.InvariantCulture) / 60.0,
                    LonBegin = double.Parse(field[13], CultureInfo.InvariantCulture) / 60.0,
                    LatEnd = double.Parse(field[14], CultureInfo.InvariantCulture) / 60.0,
                    LonEnd = double.Parse(field[15], CultureInfo.InvariantCulture) / 60.0,

                    Sequence = int.Parse(field[17], CultureInfo.InvariantCulture),
                    LengthNm = double.Parse(field[18], CultureInfo.InvariantCulture),
                    ParityColour = int.Parse(field[19], CultureInfo.InvariantCulture)
                };

                currentTrajectory.Segments.Add(segment);
                lastLineNumber = i + 1;

                // Airport (ICAO) point names are 4 characters; route points are 3 or 5.
                // Landing on a 4-letter point matching the declared destination means
                // this trajectory is complete.
                reachedDestination =
                    pointTo.Length == 4 && pointTo == currentTrajectory.Destination;
            }

            // The file ended mid-flight.
            if (currentTrajectory != null && !reachedDestination)
            {
                ReportIncidence(currentTrajectory, lastLineNumber);
            }

            return trajectoryList.ToArray();
        }

        private static void ReportIncidence(Trajectory trajectory, int lastLineNumber)
        {
            string lastPoint = trajectory.Segments[trajectory.Segments.Count - 1].PointTo;

            MessageBox.Show(
                $"Flight {trajectory.CallSign} (id {trajectory.FlightIdentifier}, " +
                $"{trajectory.Origin} -> {trajectory.Destination}) never reached its destination.\n\n" +
                $"Last segment read at line {lastLineNumber}, ending at point \"{lastPoint}\".",
                "so6 read error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
        }

        // HHMMSS, zero-padded to 6 digits (fields 5/6).
        private static TimeSpan ParseTime(string hhmmss)
        {
            return new TimeSpan(
                int.Parse(hhmmss.Substring(0, 2), CultureInfo.InvariantCulture),
                int.Parse(hhmmss.Substring(2, 2), CultureInfo.InvariantCulture),
                int.Parse(hhmmss.Substring(4, 2), CultureInfo.InvariantCulture)
            );
        }

        // YYMMDD, zero-padded to 6 digits (fields 11/12). YY <= 68 -> 20YY, else 19YY.
        private static DateTime ParseDate(string yymmdd)
        {
            int year = int.Parse(yymmdd.Substring(0, 2), CultureInfo.InvariantCulture);
            int month = int.Parse(yymmdd.Substring(2, 2), CultureInfo.InvariantCulture);
            int day = int.Parse(yymmdd.Substring(4, 2), CultureInfo.InvariantCulture);

            year = year <= 68 ? 2000 + year : 1900 + year;

            return new DateTime(year, month, day);
        }
    }
}