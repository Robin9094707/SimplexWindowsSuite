using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SimplexSuite.Core.Common;
using SimplexSuite.Core.Media;
using SimplexSuite.Core.Transcription;

namespace SimplexSuite.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true;
    private readonly Dictionary<string, CancellationTokenSource> _downloads = new();

    public SettingsPage()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        Select(ThemeBox, s.Theme);
        Select(BackdropBox, s.Backdrop);
        Select(RateBox, s.WorkingSampleRate.ToString());
        foreach (var (code, name) in WhisperModels.Languages) LanguageBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        Select(LanguageBox, s.WhisperLanguage);
        ThreadsBox.Value = s.WhisperThreads;
        VerbatimSwitch.IsOn = s.WhisperVerbatim;
        FillerBox.Text = s.FillerWords;
        MonoSwitch.IsOn = s.ForceMono;
        UndoBox.Value = s.MaxUndoMegabytes;
        FfmpegBox.Text = s.FfmpegPath ?? "";
        AboutExpander.Description = "Version " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        DataFolderCard.Description = AppPaths.DataRoot;
        BuildModels();
        _loading = false;
    }

    private static void Select(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
            if ((item.Tag as string) == tag) { box.SelectedItem = item; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void BuildModels()
    {
        ModelsExpander.Items.Clear();
        foreach (var m in WhisperModels.All)
        {
            var card = new SettingsCard { Header = m.DisplayName, Description = $"{m.Description} · {m.SizeMb} MB · {m.RamHint}" };
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (_downloads.ContainsKey(m.FileName))
            {
                var bar = new ProgressBar { Width = 160, Tag = m.FileName, VerticalAlignment = VerticalAlignment.Center };
                var cancel = new Button { Content = "Abbrechen" };
                cancel.Click += (_, _) => { if (_downloads.TryGetValue(m.FileName, out var c)) c.Cancel(); };
                panel.Children.Add(bar);
                panel.Children.Add(cancel);
            }
            else if (m.IsInstalled)
            {
                panel.Children.Add(new TextBlock { Text = "Installiert", VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.ThemeBrush("SystemFillColorSuccessBrush") });
                var del = new Button { Content = "Entfernen" };
                del.Click += (_, _) => { WhisperModels.Delete(m); BuildModels(); };
                panel.Children.Add(del);
            }
            else
            {
                var dl = new Button { Content = "Herunterladen", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                dl.Click += async (_, _) => await DownloadAsync(m);
                panel.Children.Add(dl);
            }
            card.Content = panel;
            ModelsExpander.Items.Add(card);
        }
        bool old = _loading;
        _loading = true;
        DefaultModelBox.Items.Clear();
        foreach (var m in WhisperModels.All)
            DefaultModelBox.Items.Add(new ComboBoxItem { Content = m.DisplayName + (m.IsInstalled ? "" : " (nicht installiert)"), Tag = m.FileName });
        Select(DefaultModelBox, AppSettings.Current.WhisperModel);
        _loading = old;
    }

    private async Task DownloadAsync(WhisperModelInfo m)
    {
        var cts = new CancellationTokenSource();
        _downloads[m.FileName] = cts;
        BuildModels();
        var progress = new Progress<double>(v =>
        {
            foreach (var card in ModelsExpander.Items.OfType<SettingsCard>())
                if (card.Content is StackPanel sp)
                    foreach (var bar in sp.Children.OfType<ProgressBar>())
                        if ((bar.Tag as string) == m.FileName) bar.Value = v * 100;
        });
        try
        {
            await WhisperModels.DownloadAsync(m, progress, cts.Token);
            if (!WhisperModels.Installed.Any(x => x.FileName == AppSettings.Current.WhisperModel))
            {
                AppSettings.Current.WhisperModel = m.FileName;
                AppSettings.Current.Save();
            }
            Ui.Success("Modell installiert", m.DisplayName + " ist bereit.");
        }
        catch (Exception ex) { Ui.Error("Download fehlgeschlagen", ex); }
        finally
        {
            _downloads.Remove(m.FileName);
            BuildModels();
        }
    }

    private void ThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.Theme = Tag(ThemeBox) ?? "System";
        AppSettings.Current.Save();
        App.MainWindow?.ApplyTheme(AppSettings.Current.Theme);
    }

    private void BackdropBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.Backdrop = Tag(BackdropBox) ?? "Mica";
        AppSettings.Current.Save();
        App.MainWindow?.ApplyBackdrop(AppSettings.Current.Backdrop);
    }

    private void DefaultModelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Tag(DefaultModelBox) is not string f) return;
        AppSettings.Current.WhisperModel = f;
        AppSettings.Current.Save();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.WhisperLanguage = Tag(LanguageBox) ?? "de";
        AppSettings.Current.Save();
    }

    private void ThreadsBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        AppSettings.Current.WhisperThreads = (int)args.NewValue;
        AppSettings.Current.Save();
    }

    private void VerbatimSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.WhisperVerbatim = VerbatimSwitch.IsOn;
        AppSettings.Current.Save();
    }

    private void FillerBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.FillerWords = FillerBox.Text;
        AppSettings.Current.Save();
    }

    private void RateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.WorkingSampleRate = int.Parse(Tag(RateBox) ?? "48000");
        AppSettings.Current.Save();
    }

    private void MonoSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.ForceMono = MonoSwitch.IsOn;
        AppSettings.Current.Save();
    }

    private void UndoBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(args.NewValue)) return;
        AppSettings.Current.MaxUndoMegabytes = (int)args.NewValue;
        AppSettings.Current.Save();
    }

    private void FfmpegBox_LostFocus(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.FfmpegPath = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? null : FfmpegBox.Text.Trim();
        AppSettings.Current.Save();
        Ffmpeg.ResetCache();
    }

    private async void BrowseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var file = await Ui.PickOpenFileAsync(".exe");
        if (file == null) return;
        FfmpegBox.Text = Path.GetDirectoryName(file) ?? "";
        FfmpegBox_LostFocus(sender, e);
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.CleanupTemp();
        Ui.Success("Aufgeräumt", "Temporäre Dateien wurden gelöscht.");
    }

    private void DataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.DataRoot) { UseShellExecute = true }); } catch { }
    }
}
