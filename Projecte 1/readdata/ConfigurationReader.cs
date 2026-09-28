using Projecte_1.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.ReadData
{
    /// Clase encargada de procesar archivos CFG delimitados por punto y coma (;).
    public class ConfigurationReader : IFileReader<ConfigurationFile>
    {
        public ConfigurationFile Read(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"No se encontró el archivo: {filePath}");
            }

            var result = new ConfigurationFile();

            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();

                // Omitir líneas vacías o la cabecera de metadatos (que empieza por '#')
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                // Separar por punto y coma
                var parts = line.Split(';');

                // Verificar que existen al menos las 3 columnas esperadas
                if (parts.Length >= 3)
                {
                    var record = new ConfigurationRecord
                    {
                        AccName = parts[0].Trim(),
                        ConfigurationName = parts[1].Trim(),
                        SectorName = parts[2].Trim()
                    };

                    result.Configurations.Add(record);
                }
            }

            return result;
        }
    }
}
