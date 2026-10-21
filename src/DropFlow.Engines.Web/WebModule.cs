using System.Windows.Threading;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Web;

/// <summary>
/// Converters that render web content (WebView2). They need the UI thread's dispatcher and a writable profile folder. Create one
/// <see cref="WebPageRenderer"/>, give it to the PDF engine (so documents print to PDF and pictures without LibreOffice) and to
/// <see cref="Create"/>; disposing the returned tool closes the hidden browser.
/// </summary>
public static class WebModule
{
    public static WebPageRenderer CreateRenderer(Dispatcher uiDispatcher, string userDataFolder) => new(uiDispatcher, userDataFolder);

    public static IReadOnlyList<IConverter> Create(WebPageRenderer renderer) =>
    [
        new HtmlToPdfTool(renderer),
    ];
}
