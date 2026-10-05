using Projecte1_WPF_.Classes;
using Projecte1_WPF_.ArcGisMethods;
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
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.NTS;

namespace Projecte1_WPF_
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly IAirspacePlotter _plotter;

        public MainWindow(mainProgram program)
        {
            InitializeComponent();
            MySceneView.Scene = new Scene(BasemapStyle.ArcGISImagery);
            _plotter = new PlotAirspaces3D(MySceneView);

            Loaded += async (s, e) =>
            {
                var merged = new List<MergedAirblocks>();

                foreach (var sector in program.Elementary)   // <- placeholder name, see below
                    merged.AddRange(AirblockMerger.Merge(sector, program.Elementary));

                await _plotter.PlotAsync(merged);
            };
        }
    }
}
