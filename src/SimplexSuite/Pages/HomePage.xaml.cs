using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Transcription;

namespace SimplexSuite.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        RecentPanel.Children.Clear();
        foreach (var path in AppSettings.Current.RecentFiles.Where(File.Exists).Take(8))
        {
            var card = new CommunityToolkit.WinUI.Controls.SettingsCard
            {
                Header = Path.GetFileName(path),
                Description = Path.GetDirectoryName(path),
                IsClickEnabled = true,
                HeaderIcon = Ui.Icon(Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".pdf" => "",
                    ".ssvproj" or ".mp4" or ".mov" or ".mkv" => "",
                    _ => "",
                }),
            };
            card.Click += (_, _) => App.MainWindow?.OpenFile(path);
            RecentPanel.Children.Add(card);
        }
        NoRecentText.Visibility = RecentPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var installed = WhisperModels.Installed.Select(m => m.DisplayName).ToList();
        WhisperStatus.Text = installed.Count == 0 ? "Kein Modell installiert – hier klicken" : string.Join(", ", installed);
        FfmpegStatus.Text = "Wird geprüft …";
        var ok = await Task.Run(() => Ffmpeg.IsAvailable);
        FfmpegStatus.Text = ok ? "Bereit" : "Nicht gefunden – in den Einstellungen einen Pfad angeben";
    }

    private void OpenPodcast_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("podcast");
    private void OpenVideo_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("video");
    private void OpenPdf_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("pdf");
    private void WhisperCard_Click(object sender, RoutedEventArgs e) => App.MainWindow?.Navigate("settings");
}
