using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
}
