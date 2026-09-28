using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.Classes
{
    /// Estructura contenedora para todos los registros del archivo capacity_sector_level.csv.
    public class CapacityFile
    {
        /// Lista completa de las capacidades de los sectores parseadas desde el CSV.
        public List<CapacityRecord> Capacities { get; set; } = new List<CapacityRecord>();
    }
}
