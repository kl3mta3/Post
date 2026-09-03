using Microsoft.Win32;
using Post.Core.Publishing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Post.App;

/// <summary>Callbacks the publish dialog uses to reach the editor.</summary>
internal sealed record PublishHost(
    Action<IReadOnlyList<PublishAccount>> Publish,
    Action<PublishAccount> SignIn,
    Action Save);

/// <summary>
/// Connected destinations and what to post to each. Accounts live in an encrypted
/// store; this only edits them and hands the chosen ones to the publisher.
/// </summary>
internal sealed class PublishWindow : Window
{
    private readonly List<PublishAccount> _accounts;
    private readonly PublishHost _host;
    private readonly TabControl _tabs = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
    private readonly Grid _body = new();
    private readonly Button _addTop = new() { Content = "＋ Add account", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _status = new() { Foreground = Brushes.LightGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _publishSelected = new() { Content = "Publish selected", Padding = new Thickness(14, 6, 14, 6) };
    private readonly Button _publishAll = new() { Content = "Publish all", Padding = new Thickness(14, 6, 14, 6) };

    public PublishWindow(List<PublishAccount> accounts, PublishHost host, Window owner)
    {
        _accounts = accounts; _host = host;
        Title = "Publish"; Width = 820; Height = 700; MinWidth = 640; MinHeight = 520; Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(8, 19, 38)); Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(14) };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_addTop, Dock.Right); header.Children.Add(_addTop);
        header.Children.Add(new TextBlock { Text = "Publish", FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_publishSelected); buttons.Children.Add(_publishAll);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        root.Children.Add(_body);
        Content = root;

        _addTop.Click += (_, _) => ShowAddMenu(_addTop);
        _publishSelected.Click += (_, _) => Publish(_accounts.Where(account => account.IsSelected).ToArray());
        _publishAll.Click += (_, _) => Publish(_accounts);
        Closed += (_, _) => _host.Save();
        Refresh();
    }

    private void Publish(IReadOnlyList<PublishAccount> accounts)
    {
        if (accounts.Count == 0) { _status.Text = "No accounts are ticked."; return; }
        if (accounts.FirstOrDefault(account => string.IsNullOrWhiteSpace(account.ClientId)) is { } unregistered)
        {
            _status.Text = $"{PublishAccount.PlatformName(unregistered.Platform)} needs API access before it can upload.";
            return;
        }
        if (accounts.FirstOrDefault(account => !account.IsSignedIn) is { } missing)
        {
            _status.Text = $"Sign in to {PublishAccount.PlatformName(missing.Platform)} first.";
            return;
        }
        _host.Save();
        _host.Publish(accounts);
        Close();
    }

    private void ShowAddMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var platform in Enum.GetValues<PublishPlatform>())
        {
            var item = new MenuItem { Header = PublishAccount.PlatformName(platform) };
            var chosen = platform;
            item.Click += (_, _) => Add(chosen);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Add(PublishPlatform platform)
    {
        var account = new PublishAccount { Platform = platform };
        _accounts.Add(account);
        _host.Save();
        Refresh();
        _tabs.SelectedIndex = _accounts.Count - 1;
    }

    private void Remove(PublishAccount account)
    {
        var answer = MessageBox.Show(this, $"Remove this {PublishAccount.PlatformName(account.Platform)} account from Post? Its saved sign-in is deleted.",
            "Publish", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        _accounts.Remove(account);
        _host.Save();
        Refresh();
    }

    /// <summary>Rebuilds the tabs, or the empty state when nothing is connected.</summary>
    public void Refresh()
    {
        _body.Children.Clear();
        var any = _accounts.Count > 0;
        // The add button sits in the middle until the first account exists.
        _addTop.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _publishSelected.IsEnabled = _publishAll.IsEnabled = any;

        if (!any)
        {
            var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var add = new Button { Content = "＋ Add account", Padding = new Thickness(20, 10, 20, 10), FontSize = 14 };
            add.Click += (_, _) => ShowAddMenu(add);
            empty.Children.Add(add);
            empty.Children.Add(new TextBlock
            {
                Text = "Connect a YouTube channel or a TikTok account to publish straight from Post.",
                Foreground = Theme.Hint, FontSize = 12, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
            });
            _body.Children.Add(empty);
            _status.Text = "";
            return;
        }

        var selected = _tabs.SelectedIndex;
        _tabs.Items.Clear();
        foreach (var account in _accounts)
            _tabs.Items.Add(new TabItem { Header = account.TabHeader, Content = BuildTab(account) });
        _tabs.SelectedIndex = Math.Clamp(selected, 0, _accounts.Count - 1);
        _body.Children.Add(_tabs);
        var ticked = _accounts.Count(account => account.IsSelected);
        _status.Text = $"{ticked} of {_accounts.Count} account{(_accounts.Count == 1 ? "" : "s")} ticked for this publish.";
    }

    private FrameworkElement BuildTab(PublishAccount account)
    {
        var panel = new StackPanel { Margin = new Thickness(14) };

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var hasCredentials = !string.IsNullOrWhiteSpace(account.ClientId);
        var signIn = new Button { Content = account.IsSignedIn ? "Re-sign in" : "Sign in", Padding = new Thickness(12, 5, 12, 5), IsEnabled = hasCredentials, ToolTip = hasCredentials ? null : "Add API access first" };
        signIn.Click += (_, _) => { _host.SignIn(account); Refresh(); };
        var credentials = new Button { Content = hasCredentials ? "API access ✓" : "Add API access", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0) };
        credentials.Click += (_, _) =>
        {
            var editor = new PublishCredentialsWindow(account, OAuthBroker.RedirectDisplay(account.Platform), this);
            if (editor.ShowDialog() == true) { _host.Save(); Refresh(); }
        };
        DockPanel.SetDock(signIn, Dock.Right); top.Children.Add(signIn);
        DockPanel.SetDock(credentials, Dock.Right); top.Children.Add(credentials);
        var use = new CheckBox { Content = "Publish to this account", IsChecked = account.IsSelected, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        use.Checked += (_, _) => { account.IsSelected = true; UpdateStatus(); };
        use.Unchecked += (_, _) => { account.IsSelected = false; UpdateStatus(); };
        top.Children.Add(use);
        panel.Children.Add(top);

        panel.Children.Add(new TextBlock
        {
            Text = !hasCredentials
                ? $"No API access yet. {PublishAccount.PlatformName(account.Platform)} needs a registered app before it will accept an upload — Add API access walks through it."
                : account.IsSignedIn
                    ? account.IsExpired ? "Signed in, but the saved token has expired — sign in again before publishing." : $"Signed in as {account.DisplayName}."
                    : "API access saved. Sign in to authorize the account.",
            Foreground = account.IsSignedIn && !account.IsExpired ? Brushes.LightGreen : Brushes.Salmon,
            FontSize = 12, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap,
        });

        if (account.Platform == PublishPlatform.YouTube) BuildYouTube(panel, account);
        else BuildTikTok(panel, account);

        var delete = new Button { Content = "Delete this account", Padding = new Thickness(12, 5, 12, 5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 18, 0, 0), Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(120, 40, 48)), BorderBrush = new SolidColorBrush(Color.FromRgb(190, 80, 88)) };
        delete.Click += (_, _) => Remove(account);
        panel.Children.Add(delete);

        return new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void UpdateStatus()
        => _status.Text = $"{_accounts.Count(account => account.IsSelected)} of {_accounts.Count} account{(_accounts.Count == 1 ? "" : "s")} ticked for this publish.";

    private void BuildYouTube(Panel panel, PublishAccount account)
    {
        panel.Children.Add(Field("Title", Text(account.Title, value => account.Title = value, 100), "up to 100 characters"));
        panel.Children.Add(Field("Description", Text(account.Description, value => account.Description = value, 5000, multiline: true), "up to 5,000 characters"));
        panel.Children.Add(Field("Tags", Text(account.Tags, value => account.Tags = value, 500), "comma separated"));
        panel.Children.Add(Field("Category", Choice(YouTubeCategories, account.CategoryId, value => account.CategoryId = value), ""));
        panel.Children.Add(Field("Privacy", Choice(Privacies, ((int)account.Privacy).ToString(), value => account.Privacy = (PublishPrivacy)int.Parse(value)),
            "unaudited API projects can only publish private videos"));
        panel.Children.Add(Check("Made for kids", account.MadeForKids, value => account.MadeForKids = value));
        panel.Children.Add(Check("Notify subscribers", account.NotifySubscribers, value => account.NotifySubscribers = value));
        panel.Children.Add(FilePicker("Thumbnail", account.ThumbnailPath, value => account.ThumbnailPath = value, "Images|*.png;*.jpg;*.jpeg"));
        panel.Children.Add(new TextBlock
        {
            Text = "PNG or JPEG under 2 MB. YouTube only accepts a custom thumbnail on a channel verified at youtube.com/verify; if it refuses one, the publish summary says so.",
            Foreground = Theme.Hint, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10),
        });
    }

    private void BuildTikTok(Panel panel, PublishAccount account)
    {
        panel.Children.Add(Field("Caption", Text(account.Description, value => account.Description = value, 2200, multiline: true), "up to 2,200 characters, hashtags included"));
        panel.Children.Add(Field("Privacy", Choice(Privacies, ((int)account.Privacy).ToString(), value => account.Privacy = (PublishPrivacy)int.Parse(value)),
            "unaudited API clients can only post privately, to at most 5 users a day"));
        panel.Children.Add(Check("Allow comments", account.AllowComments, value => account.AllowComments = value));
        panel.Children.Add(Check("Allow Duet", account.AllowDuet, value => account.AllowDuet = value));
        panel.Children.Add(Check("Allow Stitch", account.AllowStitch, value => account.AllowStitch = value));
        panel.Children.Add(Check("Discloses commercial content", account.DisclosesCommercialContent, value => account.DisclosesCommercialContent = value));
        panel.Children.Add(Check("Branded content", account.BrandedContent, value => account.BrandedContent = value));
    }

    // ---- small builders -----------------------------------------------------

    private static readonly (string Label, string Value)[] Privacies =
        [("Private", "0"), ("Unlisted", "1"), ("Public", "2")];

    private static readonly (string Label, string Value)[] YouTubeCategories =
        [("People & Blogs", "22"), ("Entertainment", "24"), ("Gaming", "20"), ("Music", "10"), ("Science & Technology", "28"),
         ("Education", "27"), ("Howto & Style", "26"), ("Sports", "17"), ("Comedy", "23"), ("Film & Animation", "1")];

    private static StackPanel Field(string label, FrameworkElement control, string hint)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, 3) });
        panel.Children.Add(control);
        if (!string.IsNullOrEmpty(hint)) panel.Children.Add(new TextBlock { Text = hint, Foreground = Theme.Hint, FontSize = 11, Margin = new Thickness(2, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private static TextBox Text(string value, Action<string> apply, int maximum, bool multiline = false)
    {
        var box = new TextBox { Text = value, MaxLength = maximum, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, AcceptsReturn = multiline, MinHeight = multiline ? 90 : 0, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        box.TextChanged += (_, _) => apply(box.Text);
        return box;
    }

    private static PasswordBox Secret(string value, Action<string> apply)
    {
        var box = new PasswordBox { Password = value, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromRgb(7, 16, 35)), BorderBrush = new SolidColorBrush(Color.FromRgb(35, 52, 82)), Padding = new Thickness(7, 4, 7, 4) };
        box.PasswordChanged += (_, _) => apply(box.Password);
        return box;
    }

    private static ComboBox Choice((string Label, string Value)[] options, string current, Action<string> apply)
    {
        var box = new ComboBox();
        foreach (var option in options) box.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value });
        box.SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Value == current));
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem item && item.Tag is string value) apply(value); };
        return box;
    }

    private static CheckBox Check(string label, bool value, Action<bool> apply)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = Brushes.White, Margin = new Thickness(2, 0, 0, 8) };
        box.Checked += (_, _) => apply(true);
        box.Unchecked += (_, _) => apply(false);
        return box;
    }

    private StackPanel FilePicker(string label, string? current, Action<string?> apply, string filter)
    {
        var path = new TextBox { Text = current ?? "", IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var browse = new Button { Content = "Choose…", Padding = new Thickness(10, 4, 10, 4) };
        var clear = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4) };
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = filter, Title = label };
            if (dialog.ShowDialog(this) == true) { path.Text = dialog.FileName; apply(dialog.FileName); }
        };
        clear.Click += (_, _) => { path.Text = ""; apply(null); };
        row.Children.Add(browse); row.Children.Add(clear);
        var panel = Field(label, path, "");
        panel.Children.Add(row);
        return panel;
    }
}
