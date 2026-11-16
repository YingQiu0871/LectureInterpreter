using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace LiveCaptionsTranslator
{
    // The name of a switch stands next to it, not in it: a click on the name still works the switch, as with the
    // words of a check box.
    public static class SwitchLabel
    {
        // For every row of `root` that is a name and a switch.
        public static void Attach(Control root)
        {
            foreach (var toggle in root.GetLogicalDescendants().OfType<ToggleSwitch>())
            {
                if (toggle.Parent is Panel row && row.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock name)
                {
                    name.PointerReleased += (s, e) =>
                    {
                        if (e.InitialPressMouseButton == MouseButton.Left)
                            Press(toggle);
                    };
                }
            }
        }

        // What a click on the switch itself does: the other state, then its Click event.
        public static void Press(ToggleButton? toggle)
        {
            if (toggle == null || !toggle.IsEffectivelyEnabled || !toggle.IsEffectivelyVisible)
                return;
            toggle.IsChecked = toggle.IsChecked != true;
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
