using System.Collections.Generic;

namespace Projecte_1.Classes
{
    /// Representa la estructura de un espacio aéreo principal (líneas que inician con 'A').
    public class Airspace
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public int SubAirspacesCount { get; set; }
        public string Category { get; set; }
        public List<Sector> Sectors { get; set; } = new List<Sector>();
    }

    /// Representa un sub-espacio aéreo o sector elemental (líneas que inician con 'S').
    public class Sector
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }
}
