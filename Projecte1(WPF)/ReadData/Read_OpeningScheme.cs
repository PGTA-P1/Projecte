using Projecte1_WPF_.Classes;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.ReadData
{
    class Read_OpeningScheme
    {

        public static OpeningScheme ReadFile(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"El archivo no existe en la ruta especificada: {filePath}");
            }

            var openingScheme = new OpeningScheme();
            string[] lines = File.ReadAllLines(filePath);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Lectura de la cabecera (Primera línea iniciada con #)[cite: 1]
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
                            AiracStartDate = DateTime.ParseExact(headerParts[3].Trim(), "yyyyMMdd", CultureInfo.InvariantCulture), //[cite: 1]
                            AiracEndDate = DateTime.ParseExact(headerParts[4].Trim(), "yyyyMMdd", CultureInfo.InvariantCulture),   //[cite: 1]
                            RecordCount = int.TryParse(headerParts[5].Trim(), out int count) ? count : 0,
                            DataSource = headerParts.Length > 6 ? headerParts[6].Trim() : string.Empty
                        };
                    }
                    continue;
                }

                // Lectura de los registros de datos[cite: 1, 2]
                string[] recordParts = line.Split(';');
                if (recordParts.Length >= 6)
                {
                    var record = new OpeningSchemeRecord
                    {
                        Date = DateTime.ParseExact(recordParts[0].Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture), //[cite: 1]
                        AccName = recordParts[1].Trim(),
                        StartTime = TimeSpan.ParseExact(recordParts[2].Trim(), @"hh\:mm", CultureInfo.InvariantCulture), //[cite: 1]
                        EndTime = TimeSpan.ParseExact(recordParts[3].Trim(), @"hh\:mm", CultureInfo.InvariantCulture),   //[cite: 1]
                        ConfigurationName = recordParts[4].Trim(),
                        Origin = recordParts[5].Trim().Length > 0 ? recordParts[5].Trim()[0] : 'E'
                    };

                    openingScheme.Records.Add(record);
                }
            }

            openingScheme.BuildIndex();
            return openingScheme;
        }
    }
}