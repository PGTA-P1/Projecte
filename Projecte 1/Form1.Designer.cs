namespace Projecte_1
{
    partial class Form1
    {
        /// <summary>
        /// Variable del diseñador necesaria.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Limpiar los recursos que se estén usando.
        /// </summary>
        /// <param name="disposing">true si los recursos administrados se deben desechar; false en caso contrario.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Código generado por el Diseñador de Windows Forms

        /// <summary>
        /// Método necesario para admitir el Diseñador. No se puede modificar
        /// el contenido de este método con el editor de código.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.toolStrip1 = new System.Windows.Forms.ToolStrip();
            this.toolStripTextBox1 = new System.Windows.Forms.ToolStripTextBox();
            this.MenuInputs = new System.Windows.Forms.ToolStripDropDownButton();
            this.defaultConfigurationToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.insertFilesToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // toolStrip1
            // 
            this.toolStrip1.ImageScalingSize = new System.Drawing.Size(20, 20);
            this.toolStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripTextBox1,
            this.MenuInputs});
            this.toolStrip1.Location = new System.Drawing.Point(0, 0);
            this.toolStrip1.Name = "toolStrip1";
            this.toolStrip1.Size = new System.Drawing.Size(800, 31);
            this.toolStrip1.TabIndex = 1;
            this.toolStrip1.Text = "toolStrip1";
            // 
            // toolStripTextBox1
            // 
            this.toolStripTextBox1.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.toolStripTextBox1.Name = "toolStripTextBox1";
            this.toolStripTextBox1.Size = new System.Drawing.Size(100, 31);
            this.toolStripTextBox1.Text = "Projecte 1";
            // 
            // MenuInputs
            // 
            this.MenuInputs.AccessibleName = "Airspace Configuration";
            this.MenuInputs.DisplayStyle = System.Windows.Forms.ToolStripItemDisplayStyle.Text;
            this.MenuInputs.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.defaultConfigurationToolStripMenuItem,
            this.insertFilesToolStripMenuItem});
            this.MenuInputs.Image = ((System.Drawing.Image)(resources.GetObject("MenuInputs.Image")));
            this.MenuInputs.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.MenuInputs.Name = "MenuInputs";
            this.MenuInputs.Size = new System.Drawing.Size(175, 28);
            this.MenuInputs.Text = "Airspace Configuration";
            // 
            // defaultConfigurationToolStripMenuItem
            // 
            this.defaultConfigurationToolStripMenuItem.Name = "defaultConfigurationToolStripMenuItem";
            this.defaultConfigurationToolStripMenuItem.Size = new System.Drawing.Size(236, 26);
            this.defaultConfigurationToolStripMenuItem.Text = "Default Configuration";
            // 
            // insertFilesToolStripMenuItem
            // 
            this.insertFilesToolStripMenuItem.Name = "insertFilesToolStripMenuItem";
            this.insertFilesToolStripMenuItem.Size = new System.Drawing.Size(236, 26);
            this.insertFilesToolStripMenuItem.Text = "Insert Files";
            this.insertFilesToolStripMenuItem.Click += new System.EventHandler(this.insertFilesToolStripMenuItem_Click);
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.toolStrip1);
            this.Name = "Form1";
            this.Text = "Form1";
            this.toolStrip1.ResumeLayout(false);
            this.toolStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.ToolStrip toolStrip1;
        private System.Windows.Forms.ToolStripTextBox toolStripTextBox1;
        private System.Windows.Forms.ToolStripDropDownButton MenuInputs;
        private System.Windows.Forms.ToolStripMenuItem defaultConfigurationToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem insertFilesToolStripMenuItem;
    }
}

