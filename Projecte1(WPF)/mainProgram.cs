using System;
using System.Collections.Generic;
using System.IO;
using Projecte1_WPF_.Classes;
using Projecte1_WPF_.ReadData;

namespace Projecte1_WPF_
{
    public class mainProgram
    {
        public List<Airblock> Airblocks { get; } = new List<Airblock>();

        public void LoadAirblocks()
        {
            string fullPath = Path.Combine(AppContext.BaseDirectory, "Archives", "sectors_1608.are");
            Airblocks.Clear();
            Airblocks.AddRange(new Reader_Airblock().ReadFromFile(fullPath));
        }
    }
}