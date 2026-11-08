using System.Windows;
using System.Windows.Navigation;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class WelcomeWindow : FluentWindow
    {
        public WelcomeWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            ShellUtil.Open(e.Uri.AbsoluteUri);
            e.Handled = true;
        }
    }
}
