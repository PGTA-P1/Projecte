using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Projecte1_WPF_.Classes;

namespace Projecte1_WPF_.ReadData
{
    internal class Reader_So6
    {
        // area: if not null, only trajectories with at least one segment inside that box are stored (null = load everything).
        // The file is read line by line, and a trajectory outside the area is dropped as soon as it is complete.
        // Throws DataFileException (file, line, what is wrong) on the first line that does not follow the format, and for a
        // flight that never reaches its destination (it used to show a message box and stop reading)
        public Trajectory[] ReadFromFile(string fileName, LatLonBox area = null)
        {
            List<Trajectory> trajectoryList = new List<Trajectory>();

            Trajectory currentTrajectory = null;
            string currentCallSign = null;
            int lastLineNumber = 0;
            int lineNumber = 0;

            // Forces the very first data line to start a trajectory.
            bool reachedDestination = true;

            // Whether some segment of currentTrajectory touches the area
            bool insideArea = false;

            // The current trajectory is always the last one of the list, so dropping it is O(1)
            void DropIfOutside()
            {
                if (area != null && currentTrajectory != null && !insideArea)
                    trajectoryList.RemoveAt(trajectoryList.Count - 1);
            }

            foreach (string line in File.ReadLines(fileName))
            {
                lineNumber++;

                // Skip empty lines
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var p = new LineParser(fileName, lineNumber, line);
                string[] field = p.Fields(null, 20, "an so6 segment 'FROM_TO ORIGIN DEST TYPE hhmmss hhmmss FL FL status CALLSIGN yymmdd yymmdd lat lon lat lon id seq length parity'");

                string callSign = field[9];

                // A new trajectory starts when the callsign changes, or when the
                // previous line already landed on its destination airport.
                if (callSign != currentCallSign || reachedDestination)
                {
                    // The callsign changed but the flight we were building never
                    // reached its destination: report it and stop reading.
                    if (currentTrajectory != null && !reachedDestination)
                        throw Incidence(fileName, currentTrajectory, lastLineNumber);

                    DropIfOutside();
                    insideArea = false;

                    currentTrajectory = new Trajectory
                    {
                        FlightIdentifier = p.Long(field[16], "the flight id (17th field)"),
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
                if (splitIndex <= 0 || splitIndex == field[0].Length - 1)
                    throw p.Error($"the 1st field must be 'FROM_TO' (two point names joined by '_'), found \"{field[0]}\"");
                string pointFrom = field[0].Substring(0, splitIndex);
                string pointTo = field[0].Substring(splitIndex + 1);

                TrajectorySegment segment = new TrajectorySegment
                {
                    PointFrom = pointFrom,
                    PointTo = pointTo,

                    BeginDateTime = ParseDate(p, field[10], "the begin date (11th field)") + ParseTime(p, field[4], "the begin time (5th field)"),
                    EndDateTime = ParseDate(p, field[11], "the end date (12th field)") + ParseTime(p, field[5], "the end time (6th field)"),

                    FLBegin = p.Int(field[6], "the begin flight level (7th field)"),
                    FLEnd = p.Int(field[7], "the end flight level (8th field)"),

                    Status = p.Int(field[8], "the status (9th field)"),

                    // so6 stores lat/lon as decimal arc-minutes (degrees*60 + fractional
                    // minutes); divide by 60 to get the decimal degrees we work with.
                    LatBegin = p.Double(field[12], "the begin latitude (13th field, arc-minutes)") / 60.0,
                    LonBegin = p.Double(field[13], "the begin longitude (14th field, arc-minutes)") / 60.0,
                    LatEnd = p.Double(field[14], "the end latitude (15th field, arc-minutes)") / 60.0,
                    LonEnd = p.Double(field[15], "the end longitude (16th field, arc-minutes)") / 60.0,

                    Sequence = p.Int(field[17], "the sequence (18th field)"),
                    LengthNm = p.Double(field[18], "the length (19th field)"),
                    ParityColour = p.Int(field[19], "the parity (20th field)")
                };
                if (segment.EndDateTime < segment.BeginDateTime)
                    throw p.Error($"the segment ends ({segment.EndDateTime:dd/MM HH:mm:ss}) before it begins ({segment.BeginDateTime:dd/MM HH:mm:ss})");
                if (Math.Abs(segment.LatBegin) > 90 || Math.Abs(segment.LatEnd) > 90 || Math.Abs(segment.LonBegin) > 180 || Math.Abs(segment.LonEnd) > 180)
                    throw p.Error("a point is outside the Earth: coordinates must be arc-minutes, latitude first");

                currentTrajectory.Segments.Add(segment);
                lastLineNumber = lineNumber;

                if (area != null && !insideArea)
                    insideArea = area.Intersects(segment.LatBegin, segment.LonBegin, segment.LatEnd, segment.LonEnd);

                // Airport (ICAO) point names are 4 characters; route points are 3 or 5.
                // Landing on a 4-letter point matching the declared destination means
                // this trajectory is complete.
                reachedDestination =
                    pointTo.Length == 4 && pointTo == currentTrajectory.Destination;
            }

            // The file ended mid-flight.
            if (currentTrajectory != null && !reachedDestination)
                throw Incidence(fileName, currentTrajectory, lastLineNumber);

            if (lineNumber == 0 || trajectoryList.Count == 0 && area == null)
                throw new DataFileException(fileName, "no segment found: is this an .so6 file?");
            DropIfOutside();
            return trajectoryList.ToArray();
        }

        // A flight whose segments stop before its destination airport (the next line is another flight, or the file ends)
        private static DataFileException Incidence(string fileName, Trajectory trajectory, int lastLineNumber)
        {
            string lastPoint = trajectory.Segments[trajectory.Segments.Count - 1].PointTo;
            return new DataFileException(fileName, lastLineNumber, null,
                $"flight {trajectory.CallSign} (id {trajectory.FlightIdentifier}, {trajectory.Origin} -> {trajectory.Destination}) " +
                $"never reaches its destination: its last segment, on this line, ends at point \"{lastPoint}\"");
        }

        // HHMMSS, zero-padded to 6 digits (fields 5/6).
        private static TimeSpan ParseTime(LineParser p, string hhmmss, string field)
        {
            if (hhmmss.Length != 6 || !hhmmss.All(char.IsDigit))
                throw p.Error($"{field} must be hhmmss (6 digits), found \"{hhmmss}\"");
            int h = p.Int(hhmmss.Substring(0, 2), field), m = p.Int(hhmmss.Substring(2, 2), field), s = p.Int(hhmmss.Substring(4, 2), field);
            if (h > 23 || m > 59 || s > 59)
                throw p.Error($"{field} is not a valid time, found \"{hhmmss}\"");
            return new TimeSpan(h, m, s);
        }

        // YYMMDD, zero-padded to 6 digits (fields 11/12). YY <= 68 -> 20YY, else 19YY.
        private static DateTime ParseDate(LineParser p, string yymmdd, string field)
        {
            if (yymmdd.Length != 6 || !yymmdd.All(char.IsDigit))
                throw p.Error($"{field} must be yymmdd (6 digits), found \"{yymmdd}\"");
            int year = p.Int(yymmdd.Substring(0, 2), field);
            int month = p.Int(yymmdd.Substring(2, 2), field);
            int day = p.Int(yymmdd.Substring(4, 2), field);

            year = year <= 68 ? 2000 + year : 1900 + year;
            if (month < 1 || month > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
                throw p.Error($"{field} is not a valid date, found \"{yymmdd}\"");

            return new DateTime(year, month, day);
        }
    }
}