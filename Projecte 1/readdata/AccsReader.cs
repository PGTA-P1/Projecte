using System;
using System.Collections.Generic;
using System.IO;

namespace Projecte_1.ReadData
{
    public class AccsReader
    {
        /// <summary>
        /// Lee el archivo accs.txt. Si no se le pasa una ruta, calcula la ruta por defecto en Archives.
        /// </summary>
        public HashSet<string> ReadAccs(string filePath = null)
        {
            // Si no le pasamos ruta, busca automáticamente 'Archives/accs.txt' desde el proyecto
            if (string.IsNullOrEmpty(filePath))
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\"));
                filePath = Path.Combine(projectDir, "Archives", "accs.txt");
            }

            var accs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[ADVERTENCIA] No se encontró el archivo de filtros en: {filePath}");
                return accs;
            }

            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
                {
                    accs.Add(line);
                }
            }

            return accs;
        }
    }
}