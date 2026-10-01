using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_.Classes
{
    /// Estructura contenedora para todos los registros del archivo .cfg.
    public class ConfigurationFile
    {
        /// Lista completa de las configuraciones parseadas desde el CFG.
        public List<ConfigurationRecord> Configurations { get; set; } = new List<ConfigurationRecord>();
    }
}
