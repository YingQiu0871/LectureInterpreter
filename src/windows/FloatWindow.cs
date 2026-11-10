using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.utils
{
    // Window-level helpers of the floating windows (WPF).
    public static class FloatWindow
    {
        // Starts dragging the window by a left press anywhere except on buttons and resize handles
        // (call it from PreviewMouseLeftButtonDown, since child controls may mark the event handled).
        public static void DragFrom(Window window, MouseButtonEventArgs e)
        {
            if (FloatLock.Locked || e.LeftButton != MouseButtonState.Pressed)
                return;
            for (var node = e.OriginalSource as DependencyObject; node != null; node = Parent(node))
            {
                if (node is ButtonBase || node is Thumb)
                    return;
            }
            try
            {
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static DependencyObject? Parent(DependencyObject node)
        {
            if (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                return VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
            return LogicalTreeHelper.GetParent(node);
        }

        // Adds or removes WS_EX_TRANSPARENT (and WS_EX_LAYERED, which a transparent window needs).
        public static void ApplyClickThrough(Window window, bool through)
        {
            nint hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == 0)
                return;
            int style = WindowsAPI.GetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE);
            style = through
                ? style | WindowsAPI.WS_EX_TRANSPARENT | WindowsAPI.WS_EX_LAYERED
                : style & ~WindowsAPI.WS_EX_TRANSPARENT;
            WindowsAPI.SetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE, style);
        }
    }
}
