using Microsoft.UI.Xaml.Controls;
namespace SimplexSuite.Pages;
public sealed partial class PodcastPage : Page
{
    public PodcastPage() { InitializeComponent(); }
    public void OpenFile(string path) { }
    public void OpenFiles(IEnumerable<string> paths) { }
    public void ImportFiles(IEnumerable<string> paths) { }
    public void OpenProject(string path) { }
}
