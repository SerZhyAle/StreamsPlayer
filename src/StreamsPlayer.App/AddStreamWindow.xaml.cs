using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class AddStreamWindow : Window
{
    // A null Tag means "Auto": derive the media kind from the URL on save.
    private static readonly (string ResourceKey, MediaKind? Kind)[] MediaKindOptions =
    [
        ("MediaKindAuto", null),
        ("AudioOption", MediaKind.Audio),
        ("VideoOption", MediaKind.Video),
        ("KindRtsp", MediaKind.Rtsp)
    ];

    public AddStreamWindow(StreamChannel? channel = null)
    {
        InitializeComponent();
        PopulateMediaKinds(channel?.MediaKind);

        if (channel is null)
        {
            return;
        }

        UrlBox.Text = channel.Url;
        TitleBox.Text = channel.Title;
        CategoryBox.Text = channel.Category ?? string.Empty;
        TopicBox.Text = channel.Topic ?? string.Empty;
        LanguageBox.Text = channel.Language ?? string.Empty;
        CountryBox.Text = channel.Country ?? string.Empty;
        HomepageBox.Text = channel.Homepage ?? string.Empty;
        ProtocolBox.Text = channel.Protocol ?? string.Empty;
        FormatBox.Text = channel.Format ?? string.Empty;
        BitrateBox.Text = channel.Bitrate ?? string.Empty;
        LiveBox.IsChecked = channel.IsLive;

        SetResourceReference(TitleProperty, "EditWindowTitle");
        ConfirmButton.SetResourceReference(ContentControl.ContentProperty, "Save");
    }

    public string StreamUrl => UrlBox.Text;
    public string StreamTitle => TitleBox.Text;

    /// <summary>Explicit media-kind override, or null to auto-classify from the URL.</summary>
    public MediaKind? SelectedMediaKind => (MediaKindBox.SelectedItem as ComboBoxItem)?.Tag as MediaKind?;

    public string? MetaCategory => NullIfEmpty(CategoryBox.Text);
    public string? MetaTopic => NullIfEmpty(TopicBox.Text);
    public string? MetaLanguage => NullIfEmpty(LanguageBox.Text);
    public string? MetaCountry => NullIfEmpty(CountryBox.Text);
    public string? MetaHomepage => NullIfEmpty(HomepageBox.Text);
    public string? MetaProtocol => NullIfEmpty(ProtocolBox.Text);
    public string? MetaFormat => NullIfEmpty(FormatBox.Text);
    public string? MetaBitrate => NullIfEmpty(BitrateBox.Text);
    public bool? MetaIsLive => LiveBox.IsChecked;

    private void PopulateMediaKinds(MediaKind? current)
    {
        foreach (var (resourceKey, kind) in MediaKindOptions)
        {
            var item = new ComboBoxItem { Tag = kind };
            item.SetResourceReference(ContentControl.ContentProperty, resourceKey);
            MediaKindBox.Items.Add(item);
            if (kind == current)
            {
                MediaKindBox.SelectedItem = item;
            }
        }

        MediaKindBox.SelectedIndex = MediaKindBox.SelectedIndex < 0 ? 0 : MediaKindBox.SelectedIndex;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!StreamMediaKindClassifier.IsLaunchable(StreamUrl))
        {
            MessageBox.Show(this, LocalizationService.Get("InvalidUrl"), LocalizationService.Get("InvalidUrlTitle"));
            return;
        }

        DialogResult = true;
    }
}
