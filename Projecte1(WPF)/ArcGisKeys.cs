using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Projecte1_WPF_
{
    /// <summary>
    /// The ArcGIS licence string and API key (both free), kept out of the code and out of plain sight.
    /// The repository holds them scrambled in arcgis.keys.dat, so a clone runs with the map and no watermark while the
    /// keys are not readable as text (nor caught by key scanners). This hides them, it does not protect them: anyone
    /// reading this class can unscramble them, as with any key shipped inside a program.
    /// A readable arcgis.keys.json (git-ignored) next to the program wins over the .dat: use it to try another key, then
    /// run installer\scramble-keys.ps1 to update the .dat. The build copies both next to the program.
    /// Without either, the app still opens and computes everything, with no basemap and ArcGIS's "developer use" watermark.
    /// </summary>
    public sealed class ArcGisKeys
    {
        public const string PLAIN_FILE = "arcgis.keys.json";
        public const string SCRAMBLED_FILE = "arcgis.keys.dat";

        // Format of the .dat: PREFIX + base64 of the JSON's UTF-8 bytes XOR-ed with PHRASE (installer\scramble-keys.ps1
        // writes it the same way)
        private const string PREFIX = "P1K1:";
        private const string PHRASE = "PGTA-P1 trajectory entries and sector demand";

        public string License { get; set; }
        public string ApiKey { get; set; }

        // Why the key could not be used, for the start-up message; null = fine
        public string Problem { get; private set; }

        public static ArcGisKeys Load()
        {
            string plain = Path.Combine(AppContext.BaseDirectory, PLAIN_FILE);
            string scrambled = Path.Combine(AppContext.BaseDirectory, SCRAMBLED_FILE);
            string file = File.Exists(plain) ? PLAIN_FILE : SCRAMBLED_FILE;
            try
            {
                string json;
                if (File.Exists(plain))
                    json = File.ReadAllText(plain);
                else if (File.Exists(scrambled))
                    json = Unscramble(File.ReadAllText(scrambled));
                else
                    return new ArcGisKeys { Problem = $"{SCRAMBLED_FILE} was not found next to the program." };

                ArcGisKeys keys = JsonSerializer.Deserialize<ArcGisKeys>(json) ?? new ArcGisKeys();
                keys.License = Value(keys.License);
                keys.ApiKey = Value(keys.ApiKey);
                if (keys.ApiKey == null)
                    keys.Problem = $"{file} has no API key.";
                return keys;
            }
            catch (Exception ex)
            {
                return new ArcGisKeys { Problem = $"{file} could not be read ({ex.Message})." };
            }
        }

        private static string Unscramble(string text)
        {
            text = text.Trim();
            if (!text.StartsWith(PREFIX, StringComparison.Ordinal))
                throw new FormatException("unknown format");
            byte[] bytes = Convert.FromBase64String(text.Substring(PREFIX.Length));
            byte[] phrase = Encoding.UTF8.GetBytes(PHRASE);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] ^= phrase[i % phrase.Length];
            return Encoding.UTF8.GetString(bytes);
        }

        // Empty values and placeholders ("PUT_...") count as missing
        private static string Value(string s) =>
            string.IsNullOrWhiteSpace(s) || s.StartsWith("PUT_", StringComparison.Ordinal) ? null : s.Trim();
    }
}
