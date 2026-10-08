using System;
using System.Collections.Generic;

namespace Projecte_1.Classes
{
    /// Estructura contenedora que agrupa la totalidad de la información de un archivo .spc.
    public class SpcFile
    {
        /// Datos de la cabecera inicial del archivo y del ciclo AIRAC.
        public SpcMetadata Metadata { get; set; } = new SpcMetadata();

        /// Lista completa de espacios aéreos parseados con sus respectivos sectores.
        public List<Airspace> Airspaces { get; set; } = new List<Airspace>();
    }

    /// Representa la cabecera e información del ciclo AIRAC del archivo .spc (línea '#').
    public class SpcMetadata
    {
        public string FileType { get; set; }
        public int Version { get; set; }
        public int AiracCycle { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int RecordCount { get; set; }
        public string DataSource { get; set; }
    }
}
