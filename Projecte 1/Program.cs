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
            // 1. Obtener la ruta dinámica a la carpeta Archives
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\"));
            string archivesDir = Path.Combine(projectDir, "Archives");

            // 2. Definir las rutas de los archivos
            string capacityPath = Path.Combine(archivesDir, "capacity_sector_level.csv");
            string configPath = Path.Combine(archivesDir, "Configuration_1608.cfg");
            string spcPath = Path.Combine(archivesDir, "Airspace_1608.spc");

            Console.WriteLine("==================================================");
            Console.WriteLine("          INICIANDO CARGA Y MUESTRA DE DATOS");
            Console.WriteLine("==================================================\n");

            // 3. Procesar y mostrar cada archivo

            // --- ARCHIVO 1: Capacidad (.csv) ---
            ProcessFile("CAPACIDAD (CSV)", capacityPath, new CapacityReader(), file =>
            {
                Console.WriteLine($"Total de registros leídos: {file.Capacities.Count}\n");
                foreach (var cap in file.Capacities)
                {
                    Console.WriteLine($"  • Sector: {cap.SectorName,-15} | Capacidad Horaria: {cap.HourlyCapacity}");
                }
            });

            // --- ARCHIVO 2: Configuración (.cfg) ---
            ProcessFile("CONFIGURACIÓN (CFG)", configPath, new ConfigurationReader(), file =>
            {
                Console.WriteLine($"Total de registros leídos: {file.Configurations.Count}\n");
                foreach (var cfg in file.Configurations)
                {
                    Console.WriteLine($"  • ACC: {cfg.AccName,-8} | Config: {cfg.ConfigurationName,-15} | Sector: {cfg.SectorName}");
                }
            });

            // --- ARCHIVO 3: Espacio Aéreo (.spc) ---
            ProcessFile("ESPACIO AÉREO (SPC)", spcPath, new SpcReader(), file =>
            {
                Console.WriteLine("--- METADATOS DEL ARCHIVO ---");
                Console.WriteLine($"  Tipo: {file.Metadata.FileType} | Versión: {file.Metadata.Version} | AIRAC: {file.Metadata.AiracCycle}");
                Console.WriteLine($"  Fechas: {file.Metadata.StartDate:yyyy-MM-dd} a {file.Metadata.EndDate:yyyy-MM-dd}");
                Console.WriteLine($"  Registros declarados: {file.Metadata.RecordCount} | Fuente: {file.Metadata.DataSource}");

                Console.WriteLine($"\n--- ESPACIOS AÉREOS Y SECTORES ({file.Airspaces.Count} principales) ---\n");
                foreach (var airspace in file.Airspaces)
                {
                    Console.WriteLine($"  [A] ID: {airspace.Id,-10} | Nombre: {airspace.Name,-30} | Tipo: {airspace.Type,-6} | Sectors: {airspace.SubAirspacesCount,-6} | Cat: {airspace.Category}");
                    foreach (var sector in airspace.Sectors)
                    {
                        Console.WriteLine($"      └─ [S] Sector: {sector.Name,-15} | Tipo: {sector.Type}");
                    }
                }
            });

            Console.WriteLine("==================================================");
            Console.WriteLine("          CARGA FINALIZADA CON ÉXITO");
            Console.WriteLine("==================================================");
            Console.WriteLine("Pulsa Enter para salir...");
            Console.ReadLine();
        }

        /// Método genérico que lee un archivo e invoca una acción personalizada para imprimir su contenido.
        static void ProcessFile<T>(string fileTypeName, string filePath, IFileReader<T> reader, Action<T> printAction)
        {
            Console.WriteLine($">>> PROCESANDO: {fileTypeName} <<<\n");

            try
            {
                T fileData = reader.Read(filePath);
                printAction(fileData);
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"  [ERROR] No se encontró el archivo.\n          Ruta buscada: {ex.FileName ?? filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [ERROR INESPERADO]: {ex.Message}");
            }

            Console.WriteLine("\n" + new string('-', 70) + "\n");
        }
    }
}