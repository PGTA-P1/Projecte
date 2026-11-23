using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF.Classes
{
    // Representa un volumen individual asignado a un sector con sus niveles de vuelo
    public class SectorVolume
    {
        public char Sign { get; set; }          // Signo ('+') que indica adición del volumen
        public string VolumeName { get; set; }   // Nombre del volumen en el archivo ARE / Airblock
        public int BottomLevel { get; set; }    // Nivel de vuelo inferior (FL)
        public int TopLevel { get; set; }       // Nivel de vuelo superior (FL)

        public SectorVolume(char sign, string volumeName, int bottomLevel, int topLevel)
        {
            Sign = sign;
            VolumeName = volumeName;
            BottomLevel = bottomLevel;
            TopLevel = topLevel;
        }
    }
    public class ElementarySector
    {
        public string Name { get; set; }
        public List<SectorVolume> Volumes { get; set; }

        public ElementarySector(string name)
        {
            Name = name;
            Volumes = new List<SectorVolume>();
        }
    }
}