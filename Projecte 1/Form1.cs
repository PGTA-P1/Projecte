using Projecte_1.Classes;
using Projecte_1.ReadData;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Projecte_1
{
    public partial class Form1 : Form
    {
        private List<Airblock> airblocks = new List<Airblock>();

        public Form1()
        {
            InitializeComponent();
        }

        private void insertFilesToolStripMenuItem_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "ARE files (*.are)|*.are";
                openFileDialog.Title = "Select an ARE file";
                openFileDialog.Multiselect = false;

                if (openFileDialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                try
                {
                    Reader_Airblock reader = new Reader_Airblock();

                    Airblock[] newAirblocks =
                        reader.ReadFromFile(openFileDialog.FileName);

                    // Internal check: are there already airblocks loaded?
                    if (airblocks.Count > 0)
                    {
                        DialogResult result = MessageBox.Show(
                            "Do you want to replace the existing airblocks?\n\n" +
                            "Yes = Replace\n" +
                            "No = Merge\n" +
                            "Cancel = Cancel",
                            "Airblocks already loaded",
                            MessageBoxButtons.YesNoCancel,
                            MessageBoxIcon.Question
                        );

                        if (result == DialogResult.Cancel)
                        {
                            return;
                        }

                        if (result == DialogResult.Yes)
                        {
                            // Replace existing airblocks
                            airblocks = new List<Airblock>(newAirblocks);
                        }
                        else
                        {
                            // Merge with existing airblocks
                            airblocks.AddRange(newAirblocks);
                        }
                    }
                    else
                    {
                        // No previous airblocks, so simply load them
                        airblocks = new List<Airblock>(newAirblocks);
                    }

                    MessageBox.Show(
                        $"Successfully loaded {newAirblocks.Length} airblocks.\n\n" +
                        $"Total airblocks: {airblocks.Count}",
                        "File loaded",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error reading the file:\n{ex.Message}",
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
        }
    }
}
