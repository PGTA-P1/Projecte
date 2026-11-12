using Projecte1_WPF_.Classes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Projecte1_WPF_.ReadData
{
    class Read_OpeningScheme
    {
        private const string FORMAT = "'dd/MM/yyyy;ACC;hh:mm;hh:mm;CONFIG;E/T'";

        // accs: if not null, only the records of those ACCs are stored (null = all the ACCs).
        // With a filter, Header.RecordCount (from the file) is larger than Records.Count.
        // Throws DataFileException (file, line, what is wrong) on the first line that does not follow the format
        public static OpeningScheme ReadFile(string filePath, HashSet<string> accs = null)
        {
            if (!File.Exists(filePath))
                throw new DataFileException(filePath, "the file does not exist");

            var openingScheme = new OpeningScheme();
            int lineNumber = 0, found = 0;

            foreach (string rawLine in File.ReadLines(filePath))
            {
                lineNumber++;
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var p = new LineParser(filePath, lineNumber, rawLine);

                // Header (first line, starting with #): optional, read when it has the expected fields
                if (line.StartsWith("#"))
                {
                    string[] headerParts = line.Split(';');
                    if (headerParts.Length >= 7)
                    {
                        openingScheme.Header = new OpeningSchemeHeader
                        {
                            FileType = headerParts[0].TrimStart('#').Trim(),
                            Version = int.TryParse(headerParts[1].Trim(), out int ver) ? ver : 0,
                            AiracCycle = int.TryParse(headerParts[2].Trim(), out int cycle) ? cycle : 0,
                            AiracStartDate = p.Date(headerParts[3].Trim(), "yyyyMMdd", "the AIRAC start (header, field 4)"),
                            AiracEndDate = p.Date(headerParts[4].Trim(), "yyyyMMdd", "the AIRAC end (header, field 5)"),
                            RecordCount = int.TryParse(headerParts[5].Trim(), out int count) ? count : 0,
                            DataSource = headerParts.Length > 6 ? headerParts[6].Trim() : string.Empty
                        };
                    }
                    continue;
                }

                string[] recordParts = p.Fields(';', 6, FORMAT);
                found++;
                if (accs != null && !accs.Contains(recordParts[1].Trim()))
                    continue; // ACC not requested

                var record = new OpeningSchemeRecord
                {
                    Date = p.Date(recordParts[0].Trim(), "dd/MM/yyyy", "the date (1st field)"),
                    AccName = recordParts[1].Trim(),
                    StartTime = p.Time(recordParts[2].Trim(), @"hh\:mm", "the start (3rd field)"),
                    EndTime = p.Time(recordParts[3].Trim(), @"hh\:mm", "the end (4th field)"),
                    ConfigurationName = recordParts[4].Trim(),
                    Origin = recordParts[5].Trim().Length > 0 ? recordParts[5].Trim()[0] : 'E'
                };
                if (record.AccName.Length == 0 || record.ConfigurationName.Length == 0)
                    throw p.Error("the ACC (2nd field) and the configuration (5th field) cannot be empty");
                if (record.EndTime < record.StartTime)
                    throw p.Error($"the end {record.EndTime:hh\\:mm} is before the start {record.StartTime:hh\\:mm} (a period crossing midnight must be split into two lines)");

                openingScheme.Records.Add(record);
            }

            if (found == 0)
                throw new DataFileException(filePath, $"no opening record found: expected lines {FORMAT}");
            openingScheme.BuildIndex();
            return openingScheme;
        }
    }
}
