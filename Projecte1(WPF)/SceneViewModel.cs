using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Location;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.Tasks;
using Esri.ArcGISRuntime.UI;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Projecte1_WPF_
{
    /// <summary>
    /// Provides map data to an application
    /// </summary>
    public class SceneViewModel : INotifyPropertyChanged
    {
        public SceneViewModel()
        {
            _scene = new Scene()
            {
                Basemap = new Basemap(BasemapStyle.ArcGISImageryStandard)
            };
            //_scene.BaseSurface.ElevationSources.Add(new ArcGISTiledElevationSource(new Uri("https://elevation3d.arcgis.com/arcgis/rest/services/WorldElevation3D/Terrain3D/ImageServer")));
        }

        private Scene _scene;

        /// <summary>
        /// Gets or sets the map
        /// </summary>
        public Scene Scene
        {
            get => _scene;
            set { _scene = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Raises the <see cref="SceneViewModel.PropertyChanged" /> event
        /// </summary>
        /// <param name="propertyName">The name of the property that has changed</param>
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
