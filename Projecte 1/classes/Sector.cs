using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte_1.Classes
{
    /// Representa un sub-espacio aéreo o sector elemental (líneas que inician con 'S').
    public class Sector
    {
        /// Identificador o código del sub-espacio aéreo (ej. "EB", "BIRDCTA").
        public string Name { get; set; }

        /// Clasificación o tipo de sub-espacio (ej. "NAS", "FIR", "AUA").
        public string Type { get; set; }
    }
}
