using System.IO;
namespace LiveCaptionsTranslator.utils
{
    // Absolute locations of the settings and history files. Never rely on the current directory:
    // file dialogs (e.g. uploading slides) change it, and the settings would then be saved elsewhere.
    //
    // They live in the user's own folder (%LOCALAPPDATA%\LectureInterpreter), not next to the program:
    // installing a new version replaces the program folder, and settings kept there could be lost.
    public static class AppPaths
    {
        private const string SETTING_FILE = "setting.json";
        private const string HISTORY_FILE = "translation_history.db";

        private static readonly Lazy<string> dataDirectory = new(FindDataDirectory);

        public static string DataDirectory => dataDirectory.Value;
        public static string SettingFile => Path.Combine(DataDirectory, SETTING_FILE);
        public static string HistoryDb => Path.Combine(DataDirectory, HISTORY_FILE);

        private static string FindDataDirectory()
        {
            string programDir = AppContext.BaseDirectory;
            string userDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectureInterpreter");
            string legacyUserDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveCaptionsTranslator");
            // For tests (e.g. the desktop self-test): keep everything in a folder of their own.
            string? overrideDir = Environment.GetEnvironmentVariable("LCT_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                userDir = Path.GetFullPath(overrideDir);
                legacyUserDir = string.Empty;
            }
            MigrateLegacyDirectory(legacyUserDir, userDir);
            try
            {
                Directory.CreateDirectory(userDir);
            }
            catch (Exception)
            {
                return programDir;
            }

            // Files from installs that kept them next to the program: copy them over once.
            foreach (string name in new[] { SETTING_FILE, HISTORY_FILE })
            {
                string target = Path.Combine(userDir, name);
                string source = Path.Combine(programDir, name);
                try
                {
                    if (!File.Exists(target) && File.Exists(source) &&
                        !string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                        File.Copy(source, target);
                }
                catch (Exception)
                {
                    // Best effort: a file that cannot be copied is simply not migrated.
                }
            }
            return userDir;
        }

        private const string MIGRATED_MARKER = ".migrated";

        // The program used to be called LiveCaptionsTranslator and kept its data in a folder of that name.
        // On the first start under the new name, move that folder over (copy the files if a move is not possible).
        // "Done" is recorded by a marker file in the new folder, written only after everything was copied; if the
        // copy failed half way (no marker), the next start tries again and only fills in what is still missing.
        // The old folder is never deleted by the copy path.
        private static void MigrateLegacyDirectory(string legacyDir, string newDir)
        {
            try
            {
                if (string.IsNullOrEmpty(legacyDir) || !Directory.Exists(legacyDir) ||
                    string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.Combine(newDir, MIGRATED_MARKER)))
                    return;

                if (!Directory.Exists(newDir))
                {
                    try
                    {
                        Directory.Move(legacyDir, newDir);
                        TryWriteMarker(newDir);
                        return;
                    }
                    catch (Exception)
                    {
                        // Fall through to copying (e.g. the folder is in use or on another volume).
                    }
                }
                if (CopyDirectory(legacyDir, newDir))
                    TryWriteMarker(newDir);
            }
            catch (Exception)
            {
                // Best effort: without the old data the program simply starts fresh.
            }
        }

        private static void TryWriteMarker(string dir)
        {
            try
            {
                File.WriteAllText(Path.Combine(dir, MIGRATED_MARKER), string.Empty);
            }
            catch (Exception)
            {
                // Without the marker the next start just repeats the (harmless) merge.
            }
        }

        // Copies every file (including SQLite -wal/-shm files and subfolders) that is not yet in the target.
        // Each file goes to a temporary name first, so a failed copy never leaves a truncated file behind.
        // Returns true only if nothing failed.
        private static bool CopyDirectory(string source, string target)
        {
            bool ok = true;
            try
            {
                Directory.CreateDirectory(target);
                foreach (string stale in Directory.GetFiles(target, "*.tmp"))
                {
                    // Leftovers of an interrupted copy (only those that have a counterpart in the source).
                    if (File.Exists(Path.Combine(source, Path.GetFileNameWithoutExtension(stale))))
                        TryDelete(stale);
                }
                foreach (string file in Directory.GetFiles(source))
                {
                    string destination = Path.Combine(target, Path.GetFileName(file));
                    string temp = destination + ".tmp";
                    try
                    {
                        if (File.Exists(destination))
                            continue;
                        File.Copy(file, temp, true);
                        File.Move(temp, destination);
                    }
                    catch (Exception)
                    {
                        ok = false;
                        TryDelete(temp);
                    }
                }
                foreach (string directory in Directory.GetDirectories(source))
                {
                    if (!CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory))))
                        ok = false;
                }
            }
            catch (Exception)
            {
                ok = false;
            }
            return ok;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
                // Ignore.
            }
        }
    }
}
