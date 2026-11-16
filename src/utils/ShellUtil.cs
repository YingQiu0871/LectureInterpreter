using System.Diagnostics;

namespace LiveCaptionsTranslator.utils
{
    public static class ShellUtil
    {
        // Opens a URL (or any shell target) with its default handler.
        public static void Open(string target)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }

        // The same for a link clicked in the program: a failure (no browser set up) is shown, not thrown.
        public static void OpenLink(string url)
        {
            try
            {
                Open(url);
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] 无法打开网页。", $"{ex.Message}\n{url}", SnackbarType.Error,
                    timeout: 4, closeButton: true);
            }
        }
    }
}
