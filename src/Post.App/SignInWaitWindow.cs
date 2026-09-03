using Post.Core.Publishing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>
/// Shown while the browser has the sign-in. A platform that refuses in the browser
/// never redirects back, so without this the app would simply wait, and the person
/// would have no way to stop it short of the timeout.
/// </summary>
internal sealed class SignInWaitWindow : Window
{
    private readonly CancellationTokenSource _cancellation;

    public SignInWaitWindow(PublishPlatform platform, CancellationTokenSource cancellation, Window owner)
    {
        _cancellation = cancellation;
        var name = PublishAccount.PlatformName(platform);
        Title = $"Signing in to {name}";
        Width = 460; SizeToContent = SizeToContent.Height; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = $"Finish signing in to {name} in your browser", FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "Post is waiting for the browser to come back. If the page shows an error instead, cancel here and fix it in the platform's console — a blocked sign-in never returns.",
            Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
        });
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 0, 0, 14), Foreground = new SolidColorBrush(Color.FromRgb(76, 215, 208)), Background = new SolidColorBrush(Color.FromRgb(10, 21, 38)), BorderThickness = new Thickness(0) };
        panel.Children.Add(progress);

        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        cancel.Click += (_, _) => Close();
        panel.Children.Add(cancel);
        Content = panel;

    }

    private bool _completed;

    /// <summary>Closes without treating it as a cancellation.</summary>
    public void Finish() { _completed = true; Close(); }

    /// <summary>Closing the dialog any other way abandons the wait.</summary>
    protected override void OnClosed(EventArgs e)
    {
        if (!_completed) { try { _cancellation.Cancel(); } catch (ObjectDisposedException) { } }
        base.OnClosed(e);
    }
}
