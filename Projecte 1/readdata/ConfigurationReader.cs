using Projecte_1.Classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.ReadData
{
    public class ConfigurationReader : IFileReader<ConfigurationFile>
    {
        private readonly AccsReader _accsReader = new AccsReader();

        public ConfigurationFile Read(string configFilePath)
        {
            if (!File.Exists(configFilePath))
                throw new FileNotFoundException($"No se encontró el archivo: {configFilePath}");

            // AccsReader ya resuelve por sí mismo la ruta de accs.txt
            HashSet<string> targetAccs = _accsReader.ReadAccs();

            var result = new ConfigurationFile();

            foreach (var rawLine in File.ReadLines(configFilePath))
            {
                var line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    continue;

                var parts = line.Split(';');
                if (parts.Length >= 3)
                {
                    string accName = parts[0].Trim();
                    //Console.WriteLine(accName);

                    // FILTRO AL VUELO: Ignora líneas cuyo ACC no esté en accs.txt
                    if (targetAccs.Count > 0 && !targetAccs.Contains(accName))
                        continue;

                    result.Configurations.Add(new ConfigurationRecord
                    {
                        AccName = accName,
                        ConfigurationName = parts[1].Trim(),
                        SectorName = parts[2].Trim()
                    });
                }
            }

            return result;
        }
    }
}
