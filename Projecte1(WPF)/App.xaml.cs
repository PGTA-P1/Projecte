using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Http;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Security;
using Esri.ArcGISRuntime.UI.Controls;
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
                // Initialize the ArcGIS Maps SDK runtime before any components are created.
                ArcGISRuntimeEnvironment.Initialize(config => config
                  .UseLicense("nativelite,3000,rud4u0bartq2,none,JFBXJAY3MBHL6XCFK183")
                  .UseApiKey("AAPTaOVICXgKxKU8X_KYWLQb1qA..4ykxWY31YoZBqcd37_3mg0fqBUbABzy5GokCf8k-FyLgnq5kDX-Nj0obwuyooZqZ8abeXJie87qHq4l1WPeOcGnk81nokWv3ljnKYFtugixC3ADCibqxTMyJ7ZCcRfwHRA1WLtl67eq_E9Hkvv0ot-W8nFkFs_qcJat8wodC50h0iOH7D49c2gYdcotkd5kMhp_Ay77hJ8dAu4z_0jkk_Kj_eXzc-w83ovZXHjdUjY3Ov90DGJKK7g..AT1_vLjO4233")
                  .ConfigureAuthentication(auth => auth
                     .UseDefaultChallengeHandler() // Use the default authentication dialog
                  // .UseOAuthAuthorizeHandler(myOauthAuthorizationHandler) // Configure a custom OAuth dialog
                   )
                );
                // Enable support for TimestampOffset fields, which also changes behavior of Date fields.
                // For more information see https://links.esri.com/DotNetDateTime
                ArcGISRuntimeEnvironment.EnableTimestampOffsetSupport = true;
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
