using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Projecte1_WPF_.ReadData
{
    // Where the user's files are kept while the app runs: %TEMP%\Projecte1\session-<pid>-<time>, one copy per role
    // (<Role><extension>), so replacing a file overwrites that copy and moving the originals later breaks nothing.
    // Created on first use, deleted when the app closes; folders left by a crash (their process no longer runs) are
    // deleted at the next start
    public static class UserDataFolder
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "Projecte1");
        private static string _session;

        public static string Session
        {
            get
            {
                if (_session == null)
                {
                    _session = Path.Combine(Root, $"session-{Environment.ProcessId}-{DateTime.Now:yyyyMMddHHmmss}");
                    Directory.CreateDirectory(_session);
                }
                return _session;
            }
        }

        // Copies a file in as the given role (replacing the previous copy); returns the copy's path
        public static string CopyIn(DataRole role, string source)
        {
            string target = Path.Combine(Session, role + DataFiles.Info(role).Extension);
            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, overwrite: true);
            return target;
        }

        // Extracts a .zip into a folder of its own and returns its files (folders inside the zip included, Mac resource
        // forks skipped). The caller copies what it uses with CopyIn and then deletes the folder (DeleteFolder)
        public static (string Folder, List<string> Files) Extract(string zipPath)
        {
            string folder = Path.Combine(Session, "zip-" + DateTime.Now.Ticks);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, folder);
            }
            catch (InvalidDataException)
            {
                throw new DataFileException(zipPath, "this is not a valid .zip file (damaged, or another kind of archive)");
            }
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                                 .Where(f => !f.Contains("__MACOSX") && !Path.GetFileName(f).StartsWith("._"))
                                 .ToList();
            return (folder, files);
        }

        public static void DeleteFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException ex) { Debug.WriteLine("[UserDataFolder] " + ex.Message); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine("[UserDataFolder] " + ex.Message); }
        }

        // When the app closes
        public static void DeleteSession()
        {
            if (_session != null)
                DeleteFolder(_session);
        }

        // At start-up: folders of sessions whose process is gone (the app crashed or was killed)
        public static void DeleteStale()
        {
            if (!Directory.Exists(Root))
                return;
            foreach (string dir in Directory.GetDirectories(Root, "session-*"))
            {
                string[] parts = Path.GetFileName(dir).Split('-');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int pid) && IsRunning(pid) && pid != Environment.ProcessId)
                    continue; // another instance is using it
                DeleteFolder(dir);
            }
        }

        private static bool IsRunning(int pid)
        {
            try { using (Process.GetProcessById(pid)) return true; }
            catch (ArgumentException) { return false; }
        }
    }
}
