using Microsoft.UI.Xaml.Controls;
namespace SimplexSuite.Pages;
public sealed partial class PdfPage : Page
{
    public PdfPage() { InitializeComponent(); }
    public void OpenFile(string path) { }
    public void OpenFiles(IEnumerable<string> paths) { }
    public void ImportFiles(IEnumerable<string> paths) { }
    public void OpenProject(string path) { }
}
