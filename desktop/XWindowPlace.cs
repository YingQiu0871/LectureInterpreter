using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace LiveCaptionsTranslator
{
    // Where a window is on an X11 screen at this moment, asked of the X server itself. A window's `Position` is
    // where the window manager last said it was, and not every window manager says anything while the user drags
    // a window (openbox tells where it is when they let go of it).
    public static class XWindowPlace
    {
        private static nint display;
        // No connection to the X server, or no libX11: not tried again with every caption.
        private static bool unavailable;

        [DllImport("libX11.so.6")]
        private static extern nint XOpenDisplay(nint name);

        [DllImport("libX11.so.6")]
        private static extern nint XDefaultRootWindow(nint display);

        [DllImport("libX11.so.6")]
        private static extern int XTranslateCoordinates(nint display, nint source, nint destination,
            int sourceX, int sourceY, out int x, out int y, out nint child);

        // The top left corner of the window, in pixels of the screen. Null where it cannot be asked (not X11, or
        // the window is gone). Called on the UI thread only.
        public static PixelPoint? Of(Window window)
        {
            if (unavailable || !OperatingSystem.IsLinux())
                return null;
            try
            {
                var handle = window.TryGetPlatformHandle();
                if (handle == null || handle.Handle == 0 || handle.HandleDescriptor != "XID")
                    return null;
                if (display == 0)
                    display = XOpenDisplay(0);
                if (display == 0)
                {
                    unavailable = true;
                    return null;
                }
                bool found = XTranslateCoordinates(display, handle.Handle, XDefaultRootWindow(display), 0, 0,
                    out int x, out int y, out _) != 0;
                return found ? new PixelPoint(x, y) : null;
            }
            catch (Exception)
            {
                unavailable = true;
                return null;
            }
        }
    }
}
