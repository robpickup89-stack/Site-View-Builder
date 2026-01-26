using System.Drawing;
using System.Windows.Forms;

namespace Site_View_v2
{
    partial class Form1
    {
        private ToolStrip toolStrip1;
        private Panel panelCanvasHost;
        private TextBox textBoxIni;

        private void InitializeComponent()
        {
            this.toolStrip1 = new System.Windows.Forms.ToolStrip();
            this.panelCanvasHost = new System.Windows.Forms.Panel();
            this.textBoxIni = new System.Windows.Forms.TextBox();
            this.SuspendLayout();
            // 
            // toolStrip1
            // 
            this.toolStrip1.GripStyle = System.Windows.Forms.ToolStripGripStyle.Hidden;
            this.toolStrip1.Dock = System.Windows.Forms.DockStyle.Top;
            this.toolStrip1.Name = "toolStrip1";
            this.toolStrip1.TabIndex = 0;
            this.toolStrip1.Text = "toolStrip1";
            // 
            // panelCanvasHost
            // 
            this.panelCanvasHost.BackColor = System.Drawing.Color.FromArgb(224, 224, 224);
            this.panelCanvasHost.Dock = System.Windows.Forms.DockStyle.Fill;
            this.panelCanvasHost.Location = new System.Drawing.Point(0, 25);
            this.panelCanvasHost.Name = "panelCanvasHost";
            this.panelCanvasHost.TabIndex = 1;
            // 
            // textBoxIni
            // 
            this.textBoxIni.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.textBoxIni.Height = 150;
            this.textBoxIni.Multiline = true;
            this.textBoxIni.ReadOnly = true;
            this.textBoxIni.Visible = false;
            this.textBoxIni.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.textBoxIni.Font = new System.Drawing.Font("Consolas", 9F);
            this.textBoxIni.Name = "textBoxIni";
            this.textBoxIni.TabIndex = 2;
            // 
            // Form1
            // 
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.DoubleBuffered = true;
            this.Text = "Site Viewer v2 (WinForms - .NET 8)";
            this.ClientSize = new System.Drawing.Size(1200, 800);
            this.Icon = new System.Drawing.Icon("swarco.ico");
            this.Controls.Add(this.panelCanvasHost);
            this.Controls.Add(this.textBoxIni);
            this.Controls.Add(this.toolStrip1);
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}
