using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.Classes
{
    /// Representa un registro individual de capacidad de un sector.
    public class CapacityRecord
    {
        /// Nombre del sector (texto).
        public string SectorName { get; set; }

        /// CCapacidad horaria (número).
        public int HourlyCapacity { get; set; }
    }
}
