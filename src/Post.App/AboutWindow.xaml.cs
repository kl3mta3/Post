using Post.Core;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace Post.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        RepositoryLink.NavigateUri = new Uri($"https://github.com/{UpdateService.OfficialRepository}");
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
