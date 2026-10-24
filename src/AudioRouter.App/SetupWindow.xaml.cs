using System.Windows;
namespace AudioRouter.App;
public partial class SetupWindow : Window
{
    public SetupWindow(string targetMicrophone = "CABLE Output (VB-Audio Virtual Cable)")
    {
        InitializeComponent();
        TargetInputName.Text = targetMicrophone;
    }
    private void Download_Click(object sender, RoutedEventArgs e) => MainWindow.OpenLink("https://vb-audio.com/Cable/");
    private void Privacy_Click(object sender, RoutedEventArgs e) => MainWindow.OpenLink("ms-settings:privacy-microphone");
    private void Sound_Click(object sender, RoutedEventArgs e) => MainWindow.OpenLink("ms-settings:sound");
    private void Mixer_Click(object sender, RoutedEventArgs e) => MainWindow.OpenLink("ms-settings:apps-volume");
}
