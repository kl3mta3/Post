using Post.Core.Publishing;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Post.App;

/// <summary>
/// Walks through registering an app with one platform and collects the credentials it
/// issues. Every upload API attributes the request to a registered app, so this has to
/// happen once per platform before signing in.
/// </summary>
internal sealed class PublishCredentialsWindow : Window
{
    private readonly PublishAccount _account;
    private readonly TextBox _clientId;
    private readonly PasswordBox _clientSecret;

    private sealed record Guide(string Title, string IdLabel, string SecretLabel, string Redirect, (string Text, string? Url)[] Steps, string Caveat);

    public PublishCredentialsWindow(PublishAccount account, string redirectUri, Window owner)
    {
        _account = account;
        var guide = GuideFor(account.Platform, redirectUri);
        Title = $"{PublishAccount.PlatformName(account.Platform)} API access";
        Width = 660; Height = 640; MinWidth = 520; MinHeight = 480; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = guide.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock
        {
            Text = "Signing in proves who you are; these credentials say which app is asking. Both are needed to upload, and they are stored encrypted on this machine.",
            Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        });

        var step = 1;
        foreach (var (text, url) in guide.Steps)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 7), Foreground = Brushes.White };
            line.Inlines.Add(new Run($"{step++}.  ") { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(142, 201, 236)) });
            line.Inlines.Add(new Run(text));
            if (url is not null)
            {
                line.Inlines.Add(new Run("  "));
                var link = new Hyperlink(new Run(url)) { NavigateUri = new Uri(url), Foreground = new SolidColorBrush(Color.FromRgb(76, 215, 208)) };
                link.RequestNavigate += OpenLink;
                line.Inlines.Add(link);
            }
            panel.Children.Add(line);
        }

        if (!string.IsNullOrEmpty(guide.Redirect))
        {
            panel.Children.Add(new TextBlock { Text = "Redirect URI to register", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 3) });
            panel.Children.Add(new TextBox { Text = guide.Redirect, IsReadOnly = true, Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock
            {
                Text = "Post opens a loopback port for the sign-in and picks a free one each time, because Windows reserves whole port ranges. Register the address exactly as shown.",
                Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10),
            });
        }

        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(38, 27, 12)), BorderBrush = new SolidColorBrush(Color.FromRgb(120, 88, 32)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 16),
            Child = new TextBlock { Text = guide.Caveat, Foreground = new SolidColorBrush(Color.FromRgb(240, 205, 140)), FontSize = 12, TextWrapping = TextWrapping.Wrap },
        });

        _clientId = new TextBox { Text = account.ClientId ?? "", MaxLength = 300 };
        _clientSecret = new PasswordBox
        {
            Password = account.ClientSecret ?? "", Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(7, 16, 35)), BorderBrush = new SolidColorBrush(Color.FromRgb(35, 52, 82)), Padding = new Thickness(7, 4, 7, 4),
        };
        panel.Children.Add(new TextBlock { Text = guide.IdLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(_clientId);
        panel.Children.Add(new TextBlock { Text = guide.SecretLabel, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 3) });
        panel.Children.Add(_clientSecret);

        var save = new Button { Content = "Save", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons);
        save.Click += (_, _) => { Commit(); DialogResult = true; };

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Commit()
    {
        _account.ClientId = string.IsNullOrWhiteSpace(_clientId.Text) ? null : _clientId.Text.Trim();
        _account.ClientSecret = string.IsNullOrWhiteSpace(_clientSecret.Password) ? null : _clientSecret.Password.Trim();
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    private static Guide GuideFor(PublishPlatform platform, string redirect) => platform switch
    {
        PublishPlatform.YouTube => new Guide(
            "Create a Google Cloud project for uploading",
            "Client ID", "Client secret", redirect,
            [
                ("Create or pick a project in the Google Cloud console.", "https://console.cloud.google.com/projectcreate"),
                ("Enable the YouTube Data API v3 for it.", "https://console.cloud.google.com/apis/library/youtube.googleapis.com"),
                ("Open Branding and fill in only three things: an app name, a user support email, and a developer contact email. Leave the logo, home page, privacy policy, terms and authorized domains empty — those are for verification, and a desktop app signs in over a loopback address with no domain at all. Audience stays locked until Branding is saved.", "https://console.cloud.google.com/auth/branding"),
                ("Open Audience, add the scope .../auth/youtube.upload, then add the Google account you will upload with under Test users. Without that entry the sign-in fails with “access_denied”.", "https://console.cloud.google.com/auth/audience"),
                ("Create credentials, choose OAuth client ID, and pick application type Desktop app.", "https://console.cloud.google.com/auth/clients"),
                ("Paste the client ID and client secret below.", null),
            ],
            "While Audience is in Testing, Google expires the saved sign-in after seven days and Post will ask you to sign in again; switching to In production stops that, at the cost of an “unverified app” screen you click past once. Separately, videos from a project that has not passed YouTube's API audit are locked to private with no appeal, and the quota allows roughly six uploads a day."),

        _ => new Guide(
            "Register a TikTok for Developers app",
            "Client key", "Client secret", redirect,
            [
                ("Sign up and create an app on TikTok for Developers.", "https://developers.tiktok.com/apps"),
                ("Add the Content Posting API product to the app.", "https://developers.tiktok.com/doc/content-posting-api-get-started"),
                ("Request the scopes video.publish, video.upload and user.info.basic.", null),
                ("Register the redirect URI shown below in the app's login settings.", null),
                ("Paste the client key and client secret below.", null),
            ],
            "Until TikTok audits the app, posts are private (SELF_ONLY), at most five users can post in any 24 hours, and each account must be set to private when posting. TikTok also reviews the publishing screen itself during the audit."),

    };
}
