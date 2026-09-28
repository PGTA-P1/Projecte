using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.Classes
{
    /// Representa un registro individual de configuración.
    public class ConfigurationRecord
    {
        /// Nombre del Centro de Control de Área (ACC).
        public string AccName { get; set; }

        /// Nombre de la configuración operativa.
        public string ConfigurationName { get; set; }

        /// Nombre del sector aéreo asociado.
        public string SectorName { get; set; }
    }
}
