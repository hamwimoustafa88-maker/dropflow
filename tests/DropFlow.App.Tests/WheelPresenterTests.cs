using DropFlow.App.Services;
using DropFlow.Core.Localization;
using DropFlow.Core.Wheel;
using DropFlow.Overlay;

namespace DropFlow.App.Tests;

/// <summary>How the "+ Add" slot and its list reach the overlay, in both languages (the window is created but never shown).</summary>
public class WheelPresenterTests
{
    private static readonly Wedge[] Wedges =
    [
        new("convert:jpg", WheelKind.Convert, "JPG", "convert:jpg"),
        new("img.ocr", WheelKind.Tools, "Extract text (OCR)", "img.ocr", Pinned: true),
        new(Wedge.AddId, WheelKind.Convert, "+", ""),
    ];

    private static readonly Wedge[] Addable =
    [
        new("img.compress", WheelKind.Tools, "Compress images", "img.compress", NeedsOptions: true),
        new("act.copy-path", WheelKind.Actions, "Copy Path", "act.copy-path"),
    ];

    [Theory]
    [InlineData("en", "+ Add", "Add to the Convert wheel", "Tools|Actions")]
    [InlineData("ar", "+ إضافة", "إضافة إلى عجلة التحويل", "أدوات|إجراءات")]
    public void The_add_slot_and_its_grouped_list_are_localised(string language, string addLabel, string title, string headers)
    {
        var (items, listTitle, listHeaders, entries, rtl, wheelHidden) = Sta.Run(() =>
        {
            var window = new WheelWindow();
            var presenter = new WheelPresenter(window, JsonLocalizer.Load(language));
            presenter.SetItems(WheelKind.Convert, Wedges, 1);
            var shown = window.Wheel.Items.Select(i => (i.Label, i.Role)).ToList();

            presenter.ShowAddList(WheelKind.Convert, Addable);
            return (shown, window.AddList.Title, string.Join("|", window.AddList.Headers), window.AddList.Entries.Select(e => e.Id).ToList(),
                window.AddList.FlowDirection == System.Windows.FlowDirection.RightToLeft, window.Wheel.Visibility != System.Windows.Visibility.Visible);
        });

        Assert.Equal([("JPG", WheelItemRole.Normal), ("Extract text (OCR)", WheelItemRole.Pinned), (addLabel, WheelItemRole.Add)], items);
        Assert.Equal(title, listTitle);
        Assert.Equal(headers, listHeaders);   // the empty "Convert to" group has no header
        Assert.Equal(["img.compress", "act.copy-path"], entries);
        Assert.Equal(language == "ar", rtl);
        Assert.True(wheelHidden);
    }

    [Fact]
    public void Going_back_to_the_wheel_hides_the_list()
    {
        var (listVisible, wheelVisible) = Sta.Run(() =>
        {
            var window = new WheelWindow();
            var presenter = new WheelPresenter(window, JsonLocalizer.Load("en"));
            presenter.ShowAddList(WheelKind.Tools, Addable);
            presenter.SetItems(WheelKind.Tools, Wedges, 1);
            return (window.IsAddListVisible, window.Wheel.Visibility == System.Windows.Visibility.Visible);
        });
        Assert.False(listVisible);
        Assert.True(wheelVisible);
    }
}
