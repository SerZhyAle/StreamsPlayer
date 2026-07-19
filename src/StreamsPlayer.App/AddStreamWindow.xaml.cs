using System.Windows;
using System.Windows.Controls;
using StreamsPlayer.Core;

namespace StreamsPlayer.App;

public partial class AddStreamWindow : Window
{
    public AddStreamWindow(StreamChannel? channel = null)
    {
        InitializeComponent();
        if (channel is null)
        {
            return;
        }

        UrlBox.Text = channel.Url;
        TitleBox.Text = channel.Title;
        SetResourceReference(TitleProperty, "EditWindowTitle");
        ConfirmButton.SetResourceReference(ContentControl.ContentProperty, "Save");
    }

    public string StreamUrl => UrlBox.Text;
    public string StreamTitle => TitleBox.Text;

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
