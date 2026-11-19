using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Projecte1_WPF_.ReadData
{
    // An input file that cannot be read as its format says: which file, which line (0 = the file as a whole), the text of
    // that line and what is wrong with it, in words the user can act on. Thrown by the readers at the point of failure
    // (never a generic catch), shown as is by the data files window and the load
    public class DataFileException : Exception
    {
        public string FilePath { get; }
        public int LineNumber { get; }
        public string LineText { get; }
        public string Problem { get; }

        public DataFileException(string filePath, int lineNumber, string lineText, string problem)
            : base(Describe(filePath, lineNumber, lineText, problem))
        {
            FilePath = filePath;
            LineNumber = lineNumber;
            LineText = lineText;
            Problem = problem;
        }

        // Whole-file problem (empty, wrong kind of file, nothing usable)
        public DataFileException(string filePath, string problem) : this(filePath, 0, null, problem) { }

        private static string Describe(string filePath, int lineNumber, string lineText, string problem)
        {
            string where = Path.GetFileName(filePath) + (lineNumber > 0 ? $", line {lineNumber.ToString("N0", CultureInfo.InvariantCulture)}" : "");
            string text = lineText == null ? "" : $"\n  Line: \"{(lineText.Length > 120 ? lineText.Substring(0, 120) + "…" : lineText)}\"";
            return $"{where}: {problem}{text}";
        }
    }

    // SKIP MODE of the readers (the user's choice in the data files window, per file): a reader given a 'skipped' list does
    // not stop at a bad line; it leaves out the whole thing the line belongs to (the flight, the airblock, the elementary
    // sector, the airspace, the configuration, or one capacity line) and notes it here. null = strict: the first problem
    // is thrown. The ACCs and the opening scheme have no skip mode: a hole there changes which sectors are open
    public static class Skips
    {
        public static void Add(List<string> skipped, DataFileException ex, string leftOut)
        {
            skipped.Add((ex.LineNumber > 0 ? $"line {ex.LineNumber.ToString("N0", CultureInfo.InvariantCulture)}: " : "") + ex.Problem + " - " + leftOut);
        }

        // Inputs whose reader can skip
        public static bool Supports(DataRole role) => role != DataRole.Accs && role != DataRole.OpeningScheme;

        // What is left out, for the button and the messages
        public static string Unit(DataRole role)
        {
            switch (role)
            {
                case DataRole.Trajectories: return "flight";
                case DataRole.Airblocks: return "airblock";
                case DataRole.ElementarySectors: return "elementary sector";
                case DataRole.Airspaces: return "airspace";
                case DataRole.Configurations: return "configuration";
                default: return "line";
            }
        }
    }

    // One line being parsed: typed fields with an error that names the field and shows the line
    public readonly struct LineParser
    {
        private readonly string _file;
        public int Number { get; }
        public string Text { get; }

        public LineParser(string file, int number, string text)
        {
            _file = file;
            Number = number;
            Text = text;
        }

        public DataFileException Error(string problem) => new DataFileException(_file, Number, Text, problem);

        public int Int(string value, string field)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
                throw Error($"{field} must be a whole number, found \"{value}\"");
            return result;
        }

        public long Long(string value, string field)
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result))
                throw Error($"{field} must be a whole number, found \"{value}\"");
            return result;
        }

        // Decimal point only (a decimal comma is reported, not guessed)
        public double Double(string value, string field)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                throw Error($"{field} must be a number with a decimal point, found \"{value}\"");
            // "NaN", "Infinity" and numbers too large for a double (1e999) parse without error: not usable values
            if (double.IsNaN(result) || double.IsInfinity(result))
                throw Error($"{field} must be an ordinary number, found \"{value}\" (not a number, or too large)");
            return result;
        }

        public DateTime Date(string value, string format, string field)
        {
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime result))
                throw Error($"{field} must be a date {format}, found \"{value}\"");
            return result;
        }

        public TimeSpan Time(string value, string format, string field)
        {
            if (!TimeSpan.TryParseExact(value, format, CultureInfo.InvariantCulture, out TimeSpan result))
                throw Error($"{field} must be a time {format.Replace("\\", "")}, found \"{value}\"");
            return result;
        }

        // Fields split by separator (null = whitespace), at least 'count' of them
        public string[] Fields(char? separator, int count, string expected)
        {
            string[] parts = separator == null ? Text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries) : Text.Split(separator.Value);
            if (parts.Length < count)
                throw Error($"expected {expected} ({count} fields), found {parts.Length}");
            return parts;
        }
    }
}
