using Projecte_1.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.ReadData
{
    /// Clase encargada de procesar archivos CSV delimitados por punto y coma (;) para capacidades.
    public class CapacityReader : IFileReader<CapacityFile>
    {
        public CapacityFile Read(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"No se encontró el archivo: {filePath}");
            }

            var result = new CapacityFile();
            bool isFirstLine = true;

            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Omitir la primera línea si es la cabecera (Sector;Capacity)
                if (isFirstLine)
                {
                    isFirstLine = false;
                    if (line.StartsWith("Sector", StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Separar por punto y coma
                var parts = line.Split(';');
                if (parts.Length >= 2)
                {
                    var record = new CapacityRecord
                    {
                        SectorName = parts[0].Trim(),
                        // Intentar parsear el número; si falla (por ej. formato incorrecto), asigna 0
                        HourlyCapacity = int.TryParse(parts[1].Trim(), out int cap) ? cap : 0
                    };

                    result.Capacities.Add(record);
                }
            }

            return result;
        }
    }
}
