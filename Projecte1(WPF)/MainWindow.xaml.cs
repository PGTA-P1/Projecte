using Projecte1_WPF_.Classes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Esri.ArcGISRuntime.Mapping;
using Projecte1_WPF_.ArcGisMethods;
using Esri.ArcGISRuntime.UI.Controls;

namespace Projecte1_WPF_
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow(mainProgram program)
        {
            InitializeComponent();
            MySceneView.Scene = new Scene(BasemapStyle.ArcGISImagery);

            Loaded += async (s, e) =>
            {
                if (program.Airblocks.Count > 0)
                    await PlotAirspaces.PlotAirblock(MySceneView, program.Airblocks[0]);
            };
        }
    }
}
