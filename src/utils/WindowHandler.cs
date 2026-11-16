using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    public static class WindowHandler
    {
        // Remembers where the window is. Called for every step while it is dragged or resized, so the settings
        // file is written a moment later (Setting.SaveSoon) and not once per step.
        public static Rect SaveState(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;
            // There are no bounds before the window is shown: what was saved stays.
            var bounds = window.RestoreBounds;
            if (bounds.IsEmpty)
                return bounds;

            string windowName = window.GetType().Name;
            string text = Regex.Replace(bounds.ToString(CultureInfo.InvariantCulture), @"(\d+\.\d{1})\d+", "$1");
            if (!setting.WindowBounds.TryGetValue(windowName, out string? saved) || saved != text)
            {
                setting.WindowBounds[windowName] = text;
                Setting.SaveSoon();
            }
            return bounds;
        }

        // The saved bounds, or `Rect.Empty` when there are none that can be read.
        public static Rect LoadState(Window? window, Setting? setting)
        {
            if (window == null || setting == null)
                return Rect.Empty;
            try
            {
                return setting.WindowBounds.TryGetValue(window.GetType().Name, out string? saved)
                    ? Rect.Parse(saved)
                    : Rect.Empty;
            }
            catch (Exception)
            {
                // An entry changed by hand or damaged: the window opens at its default place.
                return Rect.Empty;
            }
        }

        // Whether a window with these bounds could be seen and dragged: its top edge and a piece of its width have
        // to be on the desktop, which spans all screens (a window may well be left of or above the main screen).
        public static bool IsOnScreen(Rect bound)
        {
            if (bound.IsEmpty || double.IsNaN(bound.Left) || double.IsNaN(bound.Top) ||
                bound.Width <= 0 || bound.Height <= 0)
                return false;

            const double margin = 50;
            double left = SystemParameters.VirtualScreenLeft;
            double top = SystemParameters.VirtualScreenTop;
            double right = left + SystemParameters.VirtualScreenWidth;
            double bottom = top + SystemParameters.VirtualScreenHeight;
            // A window snapped to a screen edge lies a few pixels outside of it.
            return bound.Right > left + margin && bound.Left < right - margin &&
                   bound.Top > top - margin / 2 && bound.Top < bottom - margin;
        }

        public static void RestoreState(Window? window, Rect bound)
        {
            if (window == null || bound.IsEmpty)
                return;
            window.Top = bound.Top;
            window.Left = bound.Left;

            // The size only in the directions the window does not take from its content: the floating captions
            // are as high as their text, and as wide as they were left.
            if (window.SizeToContent is SizeToContent.Manual or SizeToContent.Height)
                window.Width = bound.Width;
            if (window.SizeToContent is SizeToContent.Manual or SizeToContent.Width)
                window.Height = bound.Height;
        }
    }
}
