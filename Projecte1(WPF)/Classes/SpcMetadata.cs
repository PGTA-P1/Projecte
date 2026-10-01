using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{
    /// Representa la cabecera e información del ciclo AIRAC del archivo .spc (línea '#').
    public class SpcMetadata
    {
        /// Identificador del tipo de archivo (ej. "AIRSPACE").
        public string FileType { get; set; }

        /// Versión del formato de archivo (ej. 2).
        public int Version { get; set; }

        /// Número identificador del ciclo AIRAC.
        public int AiracCycle { get; set; }

        /// Fecha de inicio del ciclo AIRAC en formato yyyyMMdd.
        public DateTime? StartDate { get; set; }

        /// Fecha de fin del ciclo AIRAC en formato yyyyMMdd.
        public DateTime? EndDate { get; set; }

        /// Cantidad total de registros incluidos en el archivo.
        public int RecordCount { get; set; }

        /// Nombre de la fuente u origen de los datos.
        public string DataSource { get; set; }
    }
}
