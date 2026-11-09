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

        // The program used to be called LiveCaptionsTranslator and kept its data in a folder of that name.
        // On the first start under the new name, move that folder over (copy the files if a move is not possible).
        private static void MigrateLegacyDirectory(string legacyDir, string newDir)
        {
            try
            {
                if (string.IsNullOrEmpty(legacyDir) || !Directory.Exists(legacyDir) ||
                    string.Equals(Path.GetFullPath(legacyDir), Path.GetFullPath(newDir), StringComparison.OrdinalIgnoreCase) ||
                    File.Exists(Path.Combine(newDir, SETTING_FILE)))
                    return;

                if (!Directory.Exists(newDir))
                {
                    try
                    {
                        Directory.Move(legacyDir, newDir);
                        return;
                    }
                    catch (Exception)
                    {
                        // Fall through to copying (e.g. the folder is in use or on another volume).
                    }
                }
                CopyDirectory(legacyDir, newDir);
            }
            catch (Exception)
            {
                // Best effort: without the old data the program simply starts fresh.
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                string destination = Path.Combine(target, Path.GetFileName(file));
                if (!File.Exists(destination))
                    File.Copy(file, destination);
            }
            foreach (string directory in Directory.GetDirectories(source))
                CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }
}
