using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Projecte1_WPF_.ReadData;

namespace Projecte1_WPF_
{
    public partial class DataFilesWindow : Window
    {
        public enum FileState { Missing, Checking, Ok, Error }

        // One input of the data set in the window
        public class FileRow : INotifyPropertyChanged
        {
            private static readonly Brush OkBrush = Charts.Frozen(0x2E, 0xA0, 0x43), ErrorBrush = Charts.Frozen(0xD0, 0x34, 0x2C),
                                          BusyBrush = Charts.Frozen(0x8A, 0x97, 0xA6), MissingBrush = Charts.Frozen(0xE0, 0x9A, 0x00),
                                          TextBrush = Charts.Frozen(0x40, 0x40, 0x40);

            public DataRoleInfo Info { get; set; }
            public string Path { get; private set; }
            public string SourceName { get; private set; }
            public FileState State { get; private set; } = FileState.Missing;
            public string Message { get; private set; } = "Missing: add it or use the default one";
            public int Version;   // a newer file replaces the result of a check still running

            public string FileText => Path == null ? "(no file)" : SourceName + (IsDefault ? "  (default)" : "");
            public bool IsDefault => Path != null && string.Equals(Path, DataFiles.DefaultPath(Info.Role), StringComparison.OrdinalIgnoreCase);
            public string ChooseText => Path == null ? "Choose…" : "Replace…";
            public string StateSymbol => State == FileState.Ok ? "✓" : State == FileState.Error ? "✗" : State == FileState.Checking ? "…" : "!";
            public Brush StateBrush => State == FileState.Ok ? OkBrush : State == FileState.Error ? ErrorBrush : State == FileState.Checking ? BusyBrush : MissingBrush;
            public Brush MessageBrush => State == FileState.Error ? ErrorBrush : State == FileState.Missing ? MissingBrush : TextBrush;

            public void Set(string path, string sourceName, FileState state, string message)
            {
                Path = path;
                SourceName = sourceName;
                SetState(state, message);
            }

            public void SetState(FileState state, string message)
            {
                State = state;
                Message = message;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private readonly List<FileRow> _rows;

        // The data set chosen (Load): the session copies (or defaults), with the names of the files the user picked
        public DataFiles Files { get; private set; }

        // previous: the files chosen last time, shown again (and checked again) so a fix only needs the wrong file
        public DataFilesWindow(DataFiles previous)
        {
            InitializeComponent();
            Charts.UseSoftwareRendering(this);
            _rows = DataFiles.Roles.Select(r => new FileRow { Info = r }).ToList();
            Rows.ItemsSource = _rows;
            if (previous != null)
                foreach (FileRow row in _rows)
                    if (previous[row.Info.Role] != null && File.Exists(previous[row.Info.Role]))
                        _ = CheckAsync(row, previous[row.Info.Role], previous.SourceName(row.Info.Role), copy: false);
            UpdateSummary();
        }

        private const string FILTER = "Data files (*.are, *.sls, *.spc, *.cfg, *.cos, *.so6, *.csv, *.txt, *.zip)|*.are;*.sls;*.spc;*.cfg;*.cos;*.so6;*.csv;*.txt;*.zip|All files|*.*";

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Multiselect = true, Filter = FILTER, Title = "Add data files" };
            if (dialog.ShowDialog(this) == true)
                AddPaths(dialog.FileNames);
        }

        private void AddZip_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = "Zip files (*.zip)|*.zip", Title = "Add a .zip with the data files" };
            if (dialog.ShowDialog(this) == true)
                AddPaths(dialog.FileNames);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                AddPaths(paths);
        }

        // Files and .zip files: each file goes to the input of its extension. Unknown types and a second file for the same
        // input are reported (the last one is used)
        private void AddPaths(IEnumerable<string> paths)
        {
            var notes = new List<string>();
            var files = new List<(string Path, string Name)>();
            var zipFolders = new List<string>();
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    notes.Add($"{System.IO.Path.GetFileName(path)} is a folder: add the files inside it, or a .zip");
                    continue;
                }
                if (string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var (folder, inside) = UserDataFolder.Extract(path);
                        zipFolders.Add(folder);
                        if (inside.Count == 0)
                            notes.Add($"{System.IO.Path.GetFileName(path)} is empty");
                        // Plain ASCII ("zip > file"): the name goes into the exported CSVs, which Excel reads as ANSI
                        files.AddRange(inside.Select(f => (f, System.IO.Path.GetFileName(path) + " > " + System.IO.Path.GetFileName(f))));
                    }
                    catch (DataFileException ex) { notes.Add(ex.Message); }
                    catch (IOException ex) { notes.Add($"{System.IO.Path.GetFileName(path)} cannot be opened: {ex.Message}"); }
                    continue;
                }
                files.Add((path, System.IO.Path.GetFileName(path)));
            }

            var byRole = new Dictionary<DataRole, (string Path, string Name)>();
            foreach (var (path, name) in files)
            {
                DataRole? role = DataFiles.RoleOf(path);
                if (role == null)
                {
                    notes.Add($"{name}: not used (unknown type {System.IO.Path.GetExtension(path)}; expected {string.Join(", ", DataFiles.Roles.Select(r => r.Extension))})");
                    continue;
                }
                if (byRole.TryGetValue(role.Value, out var earlier))
                    notes.Add($"two {DataFiles.Info(role.Value).Extension} files ({earlier.Name}, {name}): {name} is used for {DataFiles.Info(role.Value).Title}");
                byRole[role.Value] = (path, name);
            }

            var copies = new List<Task>();
            foreach (var kv in byRole)
                copies.Add(CheckAsync(_rows.First(r => r.Info.Role == kv.Key), kv.Value.Path, kv.Value.Name, copy: true));
            // The extracted .zip is not needed once its files are copied in
            _ = Task.WhenAll(copies).ContinueWith(_ => zipFolders.ForEach(UserDataFolder.DeleteFolder));

            var missing = _rows.Where(r => r.Path == null && !byRole.ContainsKey(r.Info.Role)).Select(r => r.Info.Title).ToList();
            if (byRole.Count > 0 && missing.Count > 0)
                notes.Add($"Still missing: {string.Join(", ", missing)}. Add them, or use the default ones.");
            NotesText.Text = string.Join("\n", notes);
            NotesBox.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Choose_Click(object sender, RoutedEventArgs e)
        {
            var row = (FileRow)((FrameworkElement)sender).Tag;
            var dialog = new OpenFileDialog
            {
                Title = $"{row.Info.Title} ({row.Info.Extension})",
                Filter = $"{row.Info.Title} (*{row.Info.Extension})|*{row.Info.Extension}|All files|*.*"
            };
            if (dialog.ShowDialog(this) != true)
                return;
            NotesBox.Visibility = Visibility.Collapsed;
            _ = CheckAsync(row, dialog.FileName, System.IO.Path.GetFileName(dialog.FileName), copy: true);
        }

        private void UseDefault_Click(object sender, RoutedEventArgs e)
        {
            var row = (FileRow)((FrameworkElement)sender).Tag;
            _ = CheckAsync(row, DataFiles.DefaultPath(row.Info.Role), row.Info.DefaultFile, copy: false);
        }

        private void UseDefaults_Click(object sender, RoutedEventArgs e)
        {
            foreach (FileRow row in _rows.Where(r => r.Path == null))
                _ = CheckAsync(row, DataFiles.DefaultPath(row.Info.Role), row.Info.DefaultFile, copy: false);
            NotesBox.Visibility = Visibility.Collapsed;
        }

        // Copies the file in (unless it is a default or already a copy) and reads it with its reader in the background
        private async Task CheckAsync(FileRow row, string source, string name, bool copy)
        {
            int version = ++row.Version;
            row.Set(row.Path, row.SourceName, FileState.Checking, copy ? "Copying and checking…" : "Checking…");
            UpdateSummary();
            string path = source;
            FileState state;
            string message;
            try
            {
                if (copy)
                    path = await Task.Run(() => UserDataFolder.CopyIn(row.Info.Role, source));
                if (version != row.Version) return;
                row.Set(path, name, FileState.Checking, "Checking…");
                message = await Task.Run(() => DataFiles.Check(row.Info.Role, path));
                state = FileState.Ok;
            }
            catch (DataFileException ex)
            {
                state = FileState.Error;
                // The error names the session copy; show the name the user knows
                message = ex.Message.Replace(System.IO.Path.GetFileName(ex.FilePath) ?? "", name);
            }
            catch (IOException ex)
            {
                state = FileState.Error;
                message = $"cannot be read: {ex.Message}";
            }
            catch (UnauthorizedAccessException ex)
            {
                state = FileState.Error;
                message = $"no permission to read it: {ex.Message}";
            }
            if (version != row.Version)
                return; // another file was chosen meanwhile
            row.Set(path, name, state, message);
            UpdateSummary();
        }

        private void UpdateSummary()
        {
            var missing = _rows.Where(r => r.State == FileState.Missing).ToList();
            var errors = _rows.Where(r => r.State == FileState.Error).ToList();
            int checking = _rows.Count(r => r.State == FileState.Checking);
            UseDefaultsBTN.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LoadBTN.IsEnabled = _rows.All(r => r.State == FileState.Ok);
            var parts = new List<string>();
            if (errors.Count > 0)
                parts.Add($"{errors.Count} with errors ({string.Join(", ", errors.Select(r => r.Info.Title))}): replace them or use the defaults.");
            if (missing.Count > 0)
                parts.Add($"{missing.Count} missing ({string.Join(", ", missing.Select(r => r.Info.Title))}).");
            if (checking > 0)
                parts.Add($"Checking {checking}…");
            SummaryText.Text = parts.Count == 0 ? "All the files are OK. Load replaces the data currently loaded." : string.Join(" ", parts);
            SummaryText.Foreground = errors.Count > 0 ? Charts.Red : Charts.Text;
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            var files = new DataFiles();
            foreach (FileRow row in _rows)
                files.Set(row.Info.Role, row.Path, row.SourceName);
            Files = files;
            DialogResult = true;
        }
    }
}
