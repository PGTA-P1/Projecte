using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Projecte_1.Classes;

namespace Projecte_1.ReadData
{
    public class SpcReader : IFileReader<SpcFile>
    {
        private readonly AccsReader _accsReader = new AccsReader();

        public SpcFile Read(string spcFilePath)
        {
            if (!File.Exists(spcFilePath))
                throw new FileNotFoundException($"No se encontró el archivo: {spcFilePath}");

            // 1. AccsReader resuelve dinámicamente la ruta de 'Archives/accs.txt'
            HashSet<string> targetAccs = _accsReader.ReadAccs();

            var result = new SpcFile();
            Airspace currentAirspace = null;
            bool isCurrentAirspaceAccepted = false;

            foreach (var rawLine in File.ReadLines(spcFilePath))
            {
                var line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Metadatos del encabezado
                if (line.StartsWith("#;AIRSPACE", StringComparison.OrdinalIgnoreCase))
                {
                    ParseMetadata(line, result.Metadata);
                    continue;
                }

                if (line.StartsWith("#"))
                    continue;

                var parts = line.Split(';');
                if (parts.Length == 0)
                    continue;

                string recordType = parts[0].Trim();

                // LÍNEA DE ESPACIO AÉREO (A)
                if (recordType.Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    string airspaceId = GetPart(parts, 1);

                    // FILTRO AL VUELO: Comprobar si el ID del espacio aéreo está en targetAccs
                    isCurrentAirspaceAccepted = targetAccs.Count == 0 || targetAccs.Contains(airspaceId);

                    if (isCurrentAirspaceAccepted)
                    {
                        currentAirspace = new Airspace
                        {
                            Id = airspaceId,
                            Name = GetPart(parts, 2),
                            Type = GetPart(parts, 3),
                            SubAirspacesCount = ParseInt(GetPart(parts, 4)),
                            Category = GetPart(parts, 5)
                        };
                        result.Airspaces.Add(currentAirspace);
                    }
                    else
                    {
                        currentAirspace = null;
                    }
                }
                // LÍNEA DE SECTOR (S)
                else if (recordType.Equals("S", StringComparison.OrdinalIgnoreCase))
                {
                    // FILTRO AL VUELO: Si el espacio 'A' fue rechazado, ignoramos todas sus líneas 'S' directamente
                    if (!isCurrentAirspaceAccepted || currentAirspace == null)
                        continue;

                    currentAirspace.Sectors.Add(new Sector
                    {
                        Name = GetPart(parts, 1),
                        Type = GetPart(parts, 2)
                    });
                }
            }

            return result;
        }

        private void ParseMetadata(string line, SpcMetadata metadata)
        {
            var parts = line.Split(';');
            metadata.FileType = GetPart(parts, 1);
            metadata.Version = ParseInt(GetPart(parts, 2));
            metadata.AiracCycle = ParseInt(GetPart(parts, 3));

            if (DateTime.TryParseExact(GetPart(parts, 4), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime startDate))
                metadata.StartDate = startDate;

            if (DateTime.TryParseExact(GetPart(parts, 5), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime endDate))
                metadata.EndDate = endDate;

            metadata.RecordCount = ParseInt(GetPart(parts, 6));
            metadata.DataSource = GetPart(parts, 7);
        }

        private string GetPart(string[] parts, int index) => index < parts.Length ? parts[index].Trim() : string.Empty;
        private int ParseInt(string value) => int.TryParse(value, out int result) ? result : 0;
    }
}