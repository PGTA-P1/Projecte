using System;
using System.IO;
using System.Text.Json;

namespace Projecte1_WPF_
{
    /// <summary>
    /// The ArcGIS licence string and API key, kept out of the code in arcgis.keys.json (in the repository, next to the
    /// csproj), so a key is changed by editing that file. The build copies it next to the program and the installer
    /// includes it. Without it the app still opens and computes everything, but the basemap does not load and ArcGIS
    /// shows its "developer use" watermark.
    /// </summary>
    public sealed class ArcGisKeys
    {
        public const string FILE = "arcgis.keys.json";

        public string License { get; set; }
        public string ApiKey { get; set; }

        // Why the key could not be used, for the start-up message; null = fine
        public string Problem { get; private set; }

        public static ArcGisKeys Load()
        {
            string path = Path.Combine(AppContext.BaseDirectory, FILE);
            try
            {
                if (!File.Exists(path))
                    return new ArcGisKeys { Problem = $"{FILE} was not found next to the program." };
                ArcGisKeys keys = JsonSerializer.Deserialize<ArcGisKeys>(File.ReadAllText(path)) ?? new ArcGisKeys();
                keys.License = Value(keys.License);
                keys.ApiKey = Value(keys.ApiKey);
                if (keys.ApiKey == null)
                    keys.Problem = $"{FILE} has no API key.";
                return keys;
            }
            catch (Exception ex)
            {
                return new ArcGisKeys { Problem = $"{FILE} could not be read ({ex.Message})." };
            }
        }

        // Empty values and placeholders ("PUT_...") count as missing
        private static string Value(string s) =>
            string.IsNullOrWhiteSpace(s) || s.StartsWith("PUT_", StringComparison.Ordinal) ? null : s.Trim();
    }
}
