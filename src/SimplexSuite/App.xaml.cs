using Microsoft.UI.Xaml;
using SimplexSuite.Core.Common;

namespace SimplexSuite;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.CleanupTemp();
        Core.Pdf.PdfFontResolver.Install();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        var args2 = Environment.GetCommandLineArgs().Skip(1).ToList();
        var page = args2.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase));
        if (page != null) MainWindow.Navigate(page[7..].ToLowerInvariant());
        var cmd = args2.FirstOrDefault(File.Exists);
        if (cmd != null) MainWindow.OpenFile(cmd);
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try
        {
            File.AppendAllText(Path.Combine(AppPaths.DataRoot, "error.log"), $"[{DateTime.Now:u}] {e.Exception}\n\n");
            MainWindow?.ShowError("Unerwarteter Fehler", e.Exception.Message);
        }
        catch { }
    }
}
