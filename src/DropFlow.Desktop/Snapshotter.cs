// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DropFlow.Core.Operations;
using DropFlow.Core.Wheel;
using DropFlow.Desktop.App;
using DropFlow.Desktop.Views;

namespace DropFlow.Desktop;

/// <summary>
/// <c>dropflow-desktop --snapshot DIR [--lang ar|en]</c>: opens the real windows (wheel for a photo on each wheel, the "+ Add" list, an
/// options form, settings, welcome, toast), renders each to a PNG with <see cref="RenderTargetBitmap"/> and quits. Used to look at the
/// UI on CI or under Xvfb without a person at the screen. Reads and writes nothing but the PNGs.
/// </summary>
internal sealed class Snapshotter(AppController app)
{
    public async Task RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var lang = app.Localizer.Language;
        try
        {
            var (window, session, presenter) = app.WheelParts;
            var sample = Path.Combine(folder, "photo.jpg");
            if (!File.Exists(sample)) await File.WriteAllBytesAsync(sample, [0xFF, 0xD8, 0xFF, 0xD9]);

            foreach (var kind in Enum.GetValues<WheelKind>())
            {
                presenter.NextInteractive = false;
                session.ShowFor([sample], kind, 400, 400);
                await SaveAsync(window, Path.Combine(folder, $"wheel-{kind.ToString().ToLowerInvariant()}-{lang}.png"));
            }

            // "+ Add" list for the convert wheel
            session.ShowFor([sample], WheelKind.Convert, 400, 400);
            var add = session.Items.ToList().FindIndex(w => w.IsAdd);
            if (add >= 0)
            {
                await session.OnItemDroppedAsync([sample], add);
                await SaveAsync(window, Path.Combine(folder, $"wheel-add-{lang}.png"));
            }
            session.Dismiss();

            // an empty wheel while a drag has not entered it yet
            session.OnKeyTapped(Core.Settings.TriggerKey.Shift, 400, 400);
            await SaveAsync(window, Path.Combine(folder, $"wheel-waiting-{lang}.png"));
            session.Dismiss();

            var compress = app.Registry.Find("img.compress", "jpg");
            var options = compress?.OptionsFor("img.compress") ?? [];
            if (options.Count > 0)
            {
                var form = new OptionsWindow(app.Localizer["wheel.tools"], "img.compress", options, app.Localizer);
                _ = form.AskAsync();
                await SaveAsync(form, Path.Combine(folder, $"options-img-compress-{lang}.png"));
                form.Close();
            }

            var pdfOptions = app.Registry.Find("pdf.compress", "pdf")?.OptionsFor("pdf.compress") ?? [];
            if (pdfOptions.Count > 0)
            {
                var form = new OptionsWindow("PDF", "pdf.compress", pdfOptions, app.Localizer);
                _ = form.AskAsync();
                await SaveAsync(form, Path.Combine(folder, $"options-pdf-compress-{lang}.png"));
                form.Close();
            }

            var settings = app.CreateSettingsWindow();
            settings.Show();
            await SaveAsync(settings, Path.Combine(folder, $"settings-{lang}.png"));
            settings.Close();

            var welcome = app.CreateWelcomeWindow();
            welcome.Show();
            await SaveAsync(welcome, Path.Combine(folder, $"welcome-{lang}.png"));
            welcome.Close();

            app.ShowToast(app.Localizer.Format("result.done", app.Localizer.Format("result.files", 1)), "photo (converted).png", null,
                [(app.Localizer["result.open"], () => { }), (app.Localizer["result.undo"], () => { })], null);
            await Task.Delay(300);
            foreach (var toast in ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Windows.OfType<ToastWindow>())
                await SaveAsync(toast, Path.Combine(folder, $"toast-{lang}.png"));

            File.Delete(sample);
            Log.Info("snapshots written to " + folder);
        }
        catch (Exception ex)
        {
            Log.Error("snapshot", ex);
            await File.WriteAllTextAsync(Path.Combine(folder, $"snapshot-error-{lang}.txt"), ex.ToString());
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static async Task SaveAsync(TopLevel window, string path)
    {
        // let layout and the first frame happen
        await Task.Delay(400);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        var size = window.ClientSize;
        var scale = window.RenderScaling;
        var pixels = new PixelSize(Math.Max(1, (int)Math.Ceiling(size.Width * scale)), Math.Max(1, (int)Math.Ceiling(size.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixels, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
#pragma warning disable CS0618 // the PNG default is what we want; the options overload is newer than this code needs
        bitmap.Save(path);
#pragma warning restore CS0618
    }
}
