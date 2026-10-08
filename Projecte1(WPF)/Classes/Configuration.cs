using System;
using System.Collections.Generic;
using Projecte1_WPF.Classes;

namespace Projecte1_WPF_.Classes
{
    // One configuration of the .cfg (identified by its name) with its sectors already split into two independent lists
    public class Configuration
    {
        /// ACC the configuration belongs to (column 1 of the .cfg, e.g. LEBLTMA). A configuration name alone is NOT unique
        /// ("1", "CONF1"... repeat across ACCs), the real key is AccName + ConfigurationName.
        public string AccName { get; set; }

        /// Nombre de la configuracion operativa.
        public string ConfigurationName { get; set; }

        /// Sectors of the configuration that are elementary sectors (.sls)
        public List<ElementarySector> Elementary { get; } = new List<ElementarySector>();

        /// Sectors of the configuration that are collapsed sectors (.spc, type CS)
        public List<Airspace> Collapsed { get; } = new List<Airspace>();

        /// Names in the .cfg that are neither elementary nor collapsed (to check the data)
        public List<string> NotFound { get; } = new List<string>();
    }

    public class ConfigurationList
    {
        public List<Configuration> Configurations { get; set; } = new List<Configuration>();
    }
}