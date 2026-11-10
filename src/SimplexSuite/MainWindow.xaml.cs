using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SimplexSuite.Core.Common;
using SimplexSuite.Pages;

namespace SimplexSuite;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, Page> _pages = new();
    public nint Hwnd { get; }

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico")); } catch { }

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min(1500, (int)(area.Width * 0.92)), h = Math.Min(960, (int)(area.Height * 0.92));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter op) op.Maximize();

        ApplyTheme(AppSettings.Current.Theme);
        ApplyBackdrop(AppSettings.Current.Backdrop);
        NavView.SelectedItem = NavView.MenuItems[0];
        NavView.Loaded += (_, _) =>
        {
            if (NavView.SettingsItem is NavigationViewItem s) { s.Content = "Einstellungen"; ToolTipService.SetToolTip(s, "Einstellungen"); }
        };
        Closed += OnClosed;
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        foreach (var p in _pages.Values)
        {
            if (p is IClosableEditor ed && ed.HasUnsavedChanges)
            {
                args.Handled = true;
                var ok = await Ui.ConfirmAsync("Ungespeicherte Änderungen",
                    "Es gibt ungespeicherte Änderungen. Trotzdem beenden?", "Beenden", "Abbrechen");
                if (!ok) return;
                foreach (var q in _pages.Values) (q as IClosableEditor)?.DiscardChanges();
                Close();
                return;
            }
        }
        foreach (var p in _pages.Values) (p as IDisposable)?.Dispose();
        AppPaths.CleanupTemp();
    }

    public void ApplyTheme(string theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        bool dark = RootGrid.ActualTheme == ElementTheme.Dark
                    || (theme == "Dark")
                    || (theme == "System" && Application.Current.RequestedTheme == ApplicationTheme.Dark);
        if (theme == "Light") dark = false;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(0x15, 255, 255, 255) : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(0x80, 255, 255, 255) : Windows.UI.Color.FromArgb(0x80, 0, 0, 0);
    }

    public void ApplyBackdrop(string kind)
    {
        SystemBackdrop = kind switch
        {
            "MicaAlt" => MicaController.IsSupported() ? new MicaBackdrop { Kind = MicaKind.BaseAlt } : new DesktopAcrylicBackdrop(),
            "Acrylic" => new DesktopAcrylicBackdrop(),
            "None" => null,
            _ => MicaController.IsSupported() ? new MicaBackdrop { Kind = MicaKind.Base } : new DesktopAcrylicBackdrop(),
        };
        RootGrid.Background = SystemBackdrop == null
            ? (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]
            : null;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "home";
        ShowPage(tag);
    }

    public T ShowPage<T>(string tag) where T : Page => (T)ShowPage(tag);

    public Page ShowPage(string tag)
    {
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "podcast" => new PodcastPage(),
                "video" => new VideoPage(),
                "pdf" => new PdfPage(),
                "settings" => new SettingsPage(),
                _ => new HomePage(),
            };
            _pages[tag] = page;
        }
        ContentFrame.Content = page;
        // Auswahl im Navigationsbereich synchronisieren
        if (tag == "settings") NavView.SelectedItem = NavView.SettingsItem;
        else
        {
            var item = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (i.Tag as string) == tag);
            if (item != null && !ReferenceEquals(NavView.SelectedItem, item)) NavView.SelectedItem = item;
        }
        return page;
    }

    public void Navigate(string tag) => ShowPage(tag);

    public void OpenFile(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".pdf") ShowPage<PdfPage>("pdf").OpenFiles(new[] { path });
        else if (ext == ".ssvproj") ShowPage<VideoPage>("video").OpenProject(path);
        else if (ext is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v") ShowPage<VideoPage>("video").ImportFiles(new[] { path });
        else if (Core.Audio.AudioIO.SupportedExtensions.Contains(ext)) ShowPage<PodcastPage>("podcast").OpenFile(path);
        else if (Core.Media.MediaProbe.IsImage(path)) ShowPage<PdfPage>("pdf").OpenFiles(new[] { path });
    }

    public void SetTitle(string? document)
    {
        TitleText.Text = string.IsNullOrEmpty(document) ? "Simplex Suite" : $"{document} – Simplex Suite";
        Title = TitleText.Text;
    }

    public void ShowMessage(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        GlobalInfo.Title = title;
        GlobalInfo.Message = message;
        GlobalInfo.Severity = severity;
        GlobalInfo.IsOpen = true;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 12 : 5);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => GlobalInfo.IsOpen = false;
        timer.Start();
    }

    public void ShowError(string title, string message) => ShowMessage(title, message, InfoBarSeverity.Error);
}

public interface IClosableEditor
{
    bool HasUnsavedChanges { get; }
    void DiscardChanges();
}
