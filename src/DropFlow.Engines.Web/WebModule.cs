using System.Windows.Threading;
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Web;

/// <summary>Converters that render web content (WebView2). They need the UI thread's dispatcher and a writable profile folder.</summary>
public static class WebModule
{
    public static IReadOnlyList<IConverter> Create(Dispatcher uiDispatcher, string userDataFolder) =>
    [
        new HtmlToPdfTool(uiDispatcher, userDataFolder),
    ];
}
