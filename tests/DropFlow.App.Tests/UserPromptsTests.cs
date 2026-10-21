using System.Windows.Threading;
using DropFlow.App.Services;
using DropFlow.Core.Localization;
using DropFlow.Core.Operations;
using DropFlow.Core.Settings;

namespace DropFlow.App.Tests;

public class UserPromptsTests
{
    [Fact]
    public void Helper_program_installs_are_off_by_default()
    {
        Assert.False(new AppSettings().OfferEngineInstalls);
    }

    [Fact]
    public async Task A_missing_engine_is_explained_and_never_offered_for_download_when_installs_are_off()
    {
        var shown = new List<string>();
        var loc = JsonLocalizer.Load("en");
        var prompts = new UserPrompts(Dispatcher.CurrentDispatcher, loc, msg => { shown.Add(msg); return Task.CompletedTask; }, () => false);

        var accepted = await prompts.ConfirmEngineInstallAsync(EngineId.LibreOffice, 350);

        Assert.False(accepted);
        var message = Assert.Single(shown);
        Assert.Contains(loc["engine.name." + EngineId.LibreOffice], message);
        Assert.Contains("Nothing is downloaded automatically", message);
    }

    [Fact]
    public void The_explanation_exists_in_arabic_too()
    {
        var ar = JsonLocalizer.Load("ar");
        Assert.NotEqual("engine.notOffered", ar["engine.notOffered"]);
        Assert.NotEqual("settings.offerInstalls", ar["settings.offerInstalls"]);
    }
}
