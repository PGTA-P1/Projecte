using Projecte1_WPF_.Classes;
using Projecte1_WPF_.ReadData;
using System;
using System.Globalization;
using System.IO;

namespace Projecte1_WPF_.ReadData
{
    /// Clase encargada de procesar archivos de formato .spc delimitados por punto y coma (;).
    public class SpcReader
    {
        /// Lee un archivo .spc y genera una estructura con sus metadatos y espacios aéreos.
        /// skipped: if not null (skip mode), an airspace with a bad line is left out whole and noted there
        public SpcFile Read(string filePath, System.Collections.Generic.List<string> skipped = null)
        {
            // Validación de existencia del archivo en la ruta indicada
            if (!File.Exists(filePath))
                throw new DataFileException(filePath, "the file does not exist");

            var result = new SpcFile();
            Airspace currentAirspace = null;
            bool skippingAirspace = false;
            int lineNumber = 0;

            // Lectura por flujo de líneas (Streaming) para asegurar bajo consumo de memoria RAM
            foreach (var rawLine in File.ReadLines(filePath))
            {
                lineNumber++;
                // Eliminar espacios adicionales al inicio y al final de la línea
                var line = rawLine.Trim();

                // Descartar líneas vacías
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var p = new LineParser(filePath, lineNumber, rawLine);

                // The file may start with a line holding only the number of airspaces
                if (lineNumber == 1 && int.TryParse(line, out _))
                    continue;

                // --------------------------------------------------------------------------
                // 1. Procesar la línea inicial de metadatos globales (#;AIRSPACE;...)
                // --------------------------------------------------------------------------
                if (line.StartsWith("#;AIRSPACE", StringComparison.OrdinalIgnoreCase))
                {
                    ParseMetadata(line, result.Metadata);
                    continue;
                }

                // --------------------------------------------------------------------------
                // 2. Omitir comentarios estándar (líneas que inician con '#')[
                // --------------------------------------------------------------------------
                if (line.StartsWith("#"))
                    continue;

                // --------------------------------------------------------------------------
                // 3. Separar por punto y coma (delimitador oficial del archivo .spc)
                // --------------------------------------------------------------------------
                var parts = line.Split(';');
                if (parts.Length == 0)
                    continue;

                // Extraer el identificador de registro (A = Airspace, S = Sector/Sub-airspace)
                string recordType = GetPart(parts, 0);
                bool isAirspace = recordType.Equals("A", StringComparison.OrdinalIgnoreCase);

                // Skip mode: the lines of an airspace left out are ignored up to the next 'A;' line
                if (skippingAirspace && !isAirspace)
                    continue;

                try
                {
                    // --- Procesar Registro 'A' (Espacio Aéreo Principal) ---
                    if (isAirspace)
                    {
                        skippingAirspace = false;
                        currentAirspace = null;
                        p.Fields(';', 4, "an airspace 'A;ID;NAME;TYPE;…'");
                        if (GetPart(parts, 1).Length == 0)
                            throw p.Error("the airspace id (2nd field) is empty");
                        currentAirspace = new Airspace
                        {
                            Id = GetPart(parts, 1),                 // Campo 2: Airspace ID
                            Name = GetPart(parts, 2),               // Campo 3: Descripción/Nombre
                            Type = GetPart(parts, 3),               // Campo 4: Tipo de espacio aéreo
                            SubAirspacesCount = ParseInt(GetPart(parts, 4)), // Campo 5: Número de sub-espacios
                            Category = GetPart(parts, 5)            // Campo 6: Categoría (ej. 'T')
                        };

                        result.Airspaces.Add(currentAirspace);
                    }
                    // --- Procesar Registro 'S' (Sub-espacio pertenenciente al último 'A') ---
                    else if (recordType.Equals("S", StringComparison.OrdinalIgnoreCase))
                    {
                        if (currentAirspace == null)
                            throw p.Error("a member line 'S;…' comes before any airspace line 'A;…'");
                        p.Fields(';', 2, "a member 'S;NAME;TYPE'");
                        if (GetPart(parts, 1).Length == 0)
                            throw p.Error("the member name (2nd field) is empty");
                        var sector = new Sector
                        {
                            Name = GetPart(parts, 1), // Campo 2: Nombre del sub-espacio
                            Type = GetPart(parts, 2)  // Campo 3: Tipo del sub-espacio
                        };

                        currentAirspace.Sectors.Add(sector);
                    }
                    else
                        throw p.Error($"unknown record '{recordType}': lines must start with 'A;' (airspace) or 'S;' (its members)");
                }
                catch (DataFileException ex) when (skipped != null)
                {
                    // The airspace the line belongs to goes whole (without one member it would be another sector)
                    if (currentAirspace != null)
                    {
                        result.Airspaces.Remove(currentAirspace);
                        Skips.Add(skipped, ex, $"airspace {currentAirspace.Id} skipped");
                    }
                    else
                        Skips.Add(skipped, ex, isAirspace ? "airspace skipped" : "line skipped");
                    currentAirspace = null;
                    skippingAirspace = true;
                }
            }

            if (result.Airspaces.Count == 0)
                throw new DataFileException(filePath, "no airspace line 'A;…' found: is this an .spc file?");
            return result;
        }

        /// Extrae la información de la cabecera principal del archivo .spc y puebla el objeto de metadatos[cite: 3].
        private void ParseMetadata(string line, SpcMetadata metadata)
        {
            var parts = line.Split(';');

            metadata.FileType = GetPart(parts, 1);          // Campo 2: AIRSPACE
            metadata.Version = ParseInt(GetPart(parts, 2));    // Campo 3: Versión
            metadata.AiracCycle = ParseInt(GetPart(parts, 3)); // Campo 4: Ciclo AIRAC

            // Conversión estricta de fecha de inicio (formato yyyyMMdd)
            if (DateTime.TryParseExact(GetPart(parts, 4), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime startDate))
            {
                metadata.StartDate = startDate;
            }

            // Conversión estricta de fecha de fin (formato yyyyMMdd)
            if (DateTime.TryParseExact(GetPart(parts, 5), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime endDate))
            {
                metadata.EndDate = endDate;
            }

            metadata.RecordCount = ParseInt(GetPart(parts, 6)); // Campo 7: Cantidad de registros
            metadata.DataSource = GetPart(parts, 7);             // Campo 8: Origen de datos
        }

        /// Devuelve el valor limpio de una posición del arreglo o una cadena vacía si la posición no existe.
        /// Previenen errores de tipo IndexOutOfRangeException.
        private string GetPart(string[] parts, int index)
        {
            return index < parts.Length ? parts[index].Trim() : string.Empty;
        }

        /// Intenta parsear un número entero. Devuelve 0 si el valor recibido es inválido.
        private int ParseInt(string value)
        {
            return int.TryParse(value, out int result) ? result : 0;
        }
    }
}