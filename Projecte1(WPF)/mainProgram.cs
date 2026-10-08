using System;
using System.Collections.Generic;
using System.IO;
using Projecte1_WPF.Classes;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.ReadData;

namespace Projecte1_WPF_
{
    public class mainProgram
    {
        public List<Airblock> Airblocks { get; } = new List<Airblock>();
        public List<ElementarySector> Elementary { get; } = new List<ElementarySector>();

        // Collapsed sectors: the 'A' records of the .spc whose type is CS (their 'S' lines are elementary sectors)
        public List<Airspace> Collapsed { get; } = new List<Airspace>();

        // Configurations of the .cfg, each with its elementary and collapsed sectors. Needs LoadElementary and LoadCollapsed first
        public List<Configuration> Configurations { get; } = new List<Configuration>();

        public void LoadConfiguration()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "Configuration_1608.cfg");
            Configurations.Clear();
            Configurations.AddRange(new ConfigurationReader().Read(path, Elementary, Collapsed).Configurations);
        }

        // A configuration name repeats across ACCs, so it is looked up by ACC + name (e.g. "LEBLTMA", "2AE")
        public Configuration FindConfiguration(string accName, string configName)
        {
            for (int i = 0; i < Configurations.Count; i++)
            {
                if (string.Equals(Configurations[i].AccName, accName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Configurations[i].ConfigurationName, configName, StringComparison.OrdinalIgnoreCase))
                    return Configurations[i];
            }
            return null;
        }

        public void LoadAirblocks()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "sectors_1608.are");
            Airblocks.Clear();
            Airblocks.AddRange(new Reader_Airblock().ReadFromFile(path));
        }

        public void LoadElementary()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "sectors_1608.sls");
            Elementary.Clear();
            Elementary.AddRange(Read_ElementarySectors.LoadFromFile(path));
        }

        public void LoadCollapsed()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Archives", "Airspace_1608.spc");
            SpcFile spc = new SpcReader().Read(path);

            Collapsed.Clear();
            for (int i = 0; i < spc.Airspaces.Count; i++)
            {
                if (spc.Airspaces[i].Type == "CS")
                    Collapsed.Add(spc.Airspaces[i]);
            }
        }
    }
}