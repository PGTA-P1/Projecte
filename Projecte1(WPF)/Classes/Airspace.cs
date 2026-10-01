using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{
    /// Representa la estructura de un espacio aéreo principal (líneas que inician con 'A')[cite: 3].
    public class Airspace
    {
        /// Identificador único del espacio aéreo (ej. "BENELUX", "BG", "BI")[cite: 3].
        public string Id { get; set; }

        /// Nombre descriptivo del espacio aéreo (ej. "GREENLAND (DENMARK)")[cite: 3].
        public string Name { get; set; }

        /// Tipo de espacio aéreo. Valores permitidos por norma: CS, CRSA, AUA, CLUS, NAS, AREA, AUAG, CRAS, REG, UNK[cite: 3].
        public string Type { get; set; }

        /// Cantidad de líneas 'S' (sectores) pertenecientes a este espacio aéreo[cite: 3].
        public int SubAirspacesCount { get; set; }

        /// Categoría del espacio aéreo ('T' = Test / sin datos)[cite: 3].
        public string Category { get; set; }

        /// Lista de sub-espacios aéreos o sectores asociados (líneas 'S')[cite: 3].
        public List<Sector> Sectors { get; set; } = new List<Sector>();
    }
}
