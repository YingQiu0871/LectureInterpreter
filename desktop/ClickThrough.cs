using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace LiveCaptionsTranslator
{
    // Best-effort mouse pass-through of a window: macOS (NSWindow ignoresMouseEvents) and Linux X11 (empty input
    // shape). Anything else, or any failure, leaves the window as it is.
    public static class ClickThrough
    {
        private const int SHAPE_INPUT = 2;
        private const int SHAPE_SET = 0;
        private const int UNSORTED = 0;

        private static nint display;

        [DllImport("/usr/lib/libobjc.dylib")]
        private static extern nint sel_registerName(string name);

        [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_bool(nint receiver, nint selector, byte flag);

        [DllImport("libX11.so.6")]
        private static extern nint XOpenDisplay(nint name);

        [DllImport("libX11.so.6")]
        private static extern int XFlush(nint display);

        [DllImport("libXext.so.6")]
        private static extern void XShapeCombineRectangles(nint display, nint window, int destKind, int xOff, int yOff,
            nint rectangles, int count, int op, int ordering);

        [DllImport("libXext.so.6")]
        private static extern void XShapeCombineMask(nint display, nint window, int destKind, int xOff, int yOff,
            nint mask, int op);

        public static void Set(Window window, bool through)
        {
            try
            {
                var handle = window.TryGetPlatformHandle();
                if (handle == null || handle.Handle == 0)
                    return;
                if (handle.HandleDescriptor == "NSWindow" && OperatingSystem.IsMacOS())
                {
                    objc_msgSend_bool(handle.Handle, sel_registerName("setIgnoresMouseEvents:"), (byte)(through ? 1 : 0));
                }
                else if (handle.HandleDescriptor == "XID" && OperatingSystem.IsLinux())
                {
                    if (display == 0)
                        display = XOpenDisplay(0);
                    if (display == 0)
                        return;
                    if (through)
                        XShapeCombineRectangles(display, handle.Handle, SHAPE_INPUT, 0, 0, 0, 0, SHAPE_SET, UNSORTED);
                    else
                        XShapeCombineMask(display, handle.Handle, SHAPE_INPUT, 0, 0, 0, SHAPE_SET);
                    XFlush(display);
                }
            }
            catch (Exception)
            {
                // No pass-through on this platform: the window just stays locked in place.
            }
        }
    }
}
