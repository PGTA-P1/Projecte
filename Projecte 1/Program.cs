using Projecte_1.Classes;
using Projecte_1.ReadData;
using System;
using System.IO;

namespace TuProyecto
{
    class Program
    {
        static void Main(string[] args)
        {
            // 1. Obtener la ruta dinámica de la carpeta Archives
            // Subimos 3 niveles desde el ejecutable (bin/Debug/netX.0) hasta la raíz del proyecto
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\"));
            string archivesDir = Path.Combine(projectDir, "Archives");

            // 2. Definir las rutas exactas de los archivos combinando el directorio con el nombre
            string capacityPath = Path.Combine(archivesDir, "capacity_sector_level.csv");
            string configPath = Path.Combine(archivesDir, "Configuration_1608_2.cfg");
            string spcPath = Path.Combine(archivesDir, "Airspace_1608.spc");

            Console.WriteLine("=== INICIANDO CARGA DE DATOS ===\n");
            Console.WriteLine($"Buscando archivos en: {archivesDir}\n");

            // 3. Procesar los archivos utilizando el método genérico
            ProcessFile("Capacidad", capacityPath, new CapacityReader(),
                file => file.Capacities.Count);

            ProcessFile("Configuración", configPath, new ConfigurationReader(),
                file => file.Configurations.Count);

            ProcessFile("SPC", spcPath, new SpcReader(),
                file => file.SpcRecords.Count);

            Console.WriteLine("\n=== CARGA DE DATOS FINALIZADA ===");
            Console.WriteLine("Pulsa Enter para salir...");
            Console.ReadLine();
        }

        /// Método genérico para procesar cualquier archivo que implemente IFileReader.
        static void ProcessFile<T>(string fileTypeName, string filePath, IFileReader<T> reader, Func<T, int> getRecordCount)
        {
            Console.WriteLine($"[Procesando {fileTypeName}] ...");

            try
            {
                T fileData = reader.Read(filePath);
                int count = getRecordCount(fileData);

                Console.WriteLine($"  -> Éxito: Se han cargado {count} registros.\n");
            }
            catch (FileNotFoundException ex)
            {
                // Ahora el error te mostrará exactamente la ruta donde está intentando buscar el archivo
                Console.WriteLine($"  -> Error: No se encontró el archivo.\n     Ruta buscada: {ex.FileName ?? filePath}\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  -> Error inesperado al leer {fileTypeName}: {ex.Message}\n");
            }
        }
    }
}