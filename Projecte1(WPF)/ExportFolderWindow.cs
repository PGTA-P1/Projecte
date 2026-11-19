using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace Projecte1_WPF_
{
    // EXPORT FOLDER (Results > Export folder…, and asked once before the first export): where the folder goes and what it
    // is called. The choice is kept between sessions (ExportFolder.Save). Built in code: two boxes and two buttons
    public class ExportFolderWindow : Window
    {
        private readonly TextBox _location = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
        private readonly TextBox _name = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
        private readonly TextBlock _result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12 };
        private readonly Button _ok = new Button { Content = "_Use this folder", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };

        // The folder chosen (after OK)
        public string Folder { get; private set; }

        public ExportFolderWindow(string current)
        {
            Title = "Export folder";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Brushes.White;
            FontFamily = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#Archivo");
            Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x30));
            Charts.UseSoftwareRendering(this);

            _location.Text = Path.GetDirectoryName(current) ?? "";
            _name.Text = Path.GetFileName(current);
            AutomationSet(_location, "Location of the export folder");
            AutomationSet(_name, "Name of the export folder");

            var browse = new Button { Content = "_Browse…", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += Browse_Click;
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            _ok.Click += Ok_Click;
            _location.TextChanged += (s, e) => Update();
            _name.TextChanged += (s, e) => Update();

            var locationRow = new DockPanel();
            DockPanel.SetDock(browse, Dock.Right);
            locationRow.Children.Add(browse);
            locationRow.Children.Add(_location);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(_ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            panel.Children.Add(new TextBlock { Text = "Where the exported files go", FontSize = 16, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = "Every export (entries, demand, analysis, sector and comparison files) is saved in this folder. It is created when the first file is saved, and remembered the next time you open the app.",
                TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x6A, 0x70)), Margin = new Thickness(0, 2, 0, 12)
            });
            panel.Children.Add(new TextBlock { Text = "Location", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
            panel.Children.Add(locationRow);
            panel.Children.Add(new TextBlock { Text = "Folder name", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });
            panel.Children.Add(_name);
            panel.Children.Add(_result);
            panel.Children.Add(buttons);
            Content = panel;
            Update();
            Loaded += (s, e) => { _name.Focus(); _name.SelectAll(); };
        }

        private static void AutomationSet(UIElement element, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(element, name);

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Location of the export folder" };
            if (Directory.Exists(_location.Text.Trim()))
                dialog.InitialDirectory = _location.Text.Trim();
            if (dialog.ShowDialog(this) == true)
                _location.Text = dialog.FolderName;
        }

        // The full path, or null with the reason in 'problem'
        private string Chosen(out string problem)
        {
            string location = _location.Text.Trim(), name = _name.Text.Trim();
            problem = null;
            if (location.Length == 0)
                problem = "Write or browse the location.";
            else if (!Path.IsPathRooted(location))
                problem = "The location must be a full path, e.g. C:\\Users\\you\\Documents.";
            else if (location.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                problem = "The location has characters a path cannot have.";
            else if (!Directory.Exists(location))
                problem = "That location does not exist.";
            else if (name.Length == 0)
                problem = "Write a name for the folder.";
            else if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problem = "A folder name cannot have any of  \\ / : * ? \" < > |";
            else if (name.EndsWith(".") || name.All(c => c == '.'))
                problem = "A folder name cannot end with a dot.";
            return problem == null ? Path.Combine(location, name) : null;
        }

        private void Update()
        {
            string folder = Chosen(out string problem);
            _ok.IsEnabled = folder != null;
            _result.Text = folder == null ? problem
                         : (Directory.Exists(folder) ? "Files will be saved in the existing folder " : "Files will be saved in the new folder ") + folder;
            _result.Foreground = folder == null ? Charts.Red : Charts.Text;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            string folder = Chosen(out _);
            if (folder == null)
                return;
            try
            {
                // Created now, so a folder that cannot be created (no permission, read-only place) is told here
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
            {
                _result.Text = "The folder cannot be created there: " + ex.Message;
                _result.Foreground = Charts.Red;
                return;
            }
            Folder = folder;
            DialogResult = true;
        }
    }

    // The export folder kept between sessions, in %APPDATA%\Projecte1\export_folder.txt (one line: the path)
    public static class ExportFolder
    {
        private static string SettingsFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Projecte1", "export_folder.txt");

        // Proposed the first time: Documents\Projecte P1 exports
        public static string Default =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projecte P1 exports");

        // The folder chosen in an earlier session, null if never chosen (or the note cannot be read)
        public static string Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return null;
                string path = File.ReadAllText(SettingsFile).Trim();
                return path.Length > 0 && Path.IsPathRooted(path) ? path : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return null;
            }
        }

        public static void Save(string folder)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile));
                File.WriteAllText(SettingsFile, folder);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Not kept for the next session: the choice still holds for this one
            }
        }
    }
}
