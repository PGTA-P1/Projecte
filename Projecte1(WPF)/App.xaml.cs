using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Http;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.UI.Controls;
using Projecte1_WPF_.Classes;
using System.Configuration;
using System.Data;
using System.Windows;

namespace Projecte1_WPF_
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            /* Authentication for ArcGIS location services:
             * Use of ArcGIS location services, including basemaps and geocoding, requires either:
             * 1) User authentication: Automatically generates a unique, short-lived access token when a user signs in to your application with their ArcGIS account
             *    giving your application permission to access the content and location services authorized to an existing ArcGIS user's account.
             * 2) API key authentication: Uses a long-lived access token to authenticate requests to location services and private content.
             *    Go to https://links.esri.com/create-an-api-key to learn how to create and manage an API key using API key credentials, and then call 
             *    .UseApiKey("Your ArcGIS location services API Key")
             *    in the initialize call below. */

            /* Licensing:
             * Production deployment of applications built with the ArcGIS Maps SDK requires you to license ArcGIS functionality.
             * For more information see https://links.esri.com/arcgis-runtime-license-and-deploy.
             * You can set the license string by calling .UseLicense(licenseString) in the initialize call below 
             * or retrieve a license dynamically after signing into a portal:
             * ArcGISRuntimeEnvironment.SetLicense(await myArcGISPortal.GetLicenseInfoAsync()); */

            try
            {
                // Initialize the ArcGIS Maps SDK runtime before any components are created. The licence string and the
                // API key come from arcgis.keys.json next to the program (ArcGisKeys)
                ArcGisKeys keys = ArcGisKeys.Load();
                ArcGISRuntimeEnvironment.Initialize(config =>
                {
                    if (keys.License != null)
                        config.UseLicense(keys.License);
                    if (keys.ApiKey != null)
                        config.UseApiKey(keys.ApiKey);
                    config.ConfigureAuthentication(auth => auth.UseDefaultChallengeHandler()); // default authentication dialog
                });
                // Enable support for TimestampOffset fields, which also changes behavior of Date fields.
                // For more information see https://links.esri.com/DotNetDateTime
                ArcGISRuntimeEnvironment.EnableTimestampOffsetSupport = true;
                // Starts empty: the data is loaded from the window (Load Data menu)
                new MainWindow(new mainProgram()).Show();
                if (keys.Problem != null)
                    MessageBox.Show("The map key is missing: " + keys.Problem + "\n\n" +
                                    "Everything works except the map imagery. Install the program with the setup from the " +
                                    "GitHub Releases page, or restore Projecte1(WPF)\\arcgis.keys.json from the repository " +
                                    "and build again.",
                                    "Map key missing", MessageBoxButton.OK, MessageBoxImage.Warning);

            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "ArcGIS Maps SDK runtime initialization failed.");

                // Exit application
                this.Shutdown();
            }
        }
    }
}