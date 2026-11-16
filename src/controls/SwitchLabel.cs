using System.Windows;
using System.Windows.Controls.Primitives;

namespace LiveCaptionsTranslator
{
    public static class SwitchLabel
    {
        // What a click on the switch itself does: the other state, then its Click event.
        public static void Press(ToggleButton? toggle)
        {
            if (toggle == null || !toggle.IsEnabled || !toggle.IsVisible)
                return;
            toggle.IsChecked = toggle.IsChecked != true;
            toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
        }
    }
}
