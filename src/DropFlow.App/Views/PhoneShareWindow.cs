using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.Core.App;
using DropFlow.Engines.Archive.Sharing;

namespace DropFlow.App.Views;

/// <summary>
/// "Send to phone": a QR code and link for the running <see cref="PhoneShareServer"/>, with what has been downloaded and when the
/// link stops working. The window owns the server: closing it (or pressing Stop) ends the share.
/// </summary>
internal sealed class PhoneShareWindow : Window
{
    private readonly UiKit _ui;
    private readonly PhoneShareServer _server;
    private readonly Dictionary<int, TextBlock> _status = [];
    private readonly TextBlock _expiry;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public PhoneShareWindow(UiKit ui, PhoneShareServer server)
    {
        _ui = ui;
        _server = server;
        ui.Style(this, 420, ui.Loc["phone.title"]);
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;

        var url = server.ListingUrl.AbsoluteUri;
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(ui.Text(ui.Loc["phone.title"], 18, weight: FontWeights.SemiBold));
        root.Children.Add(ui.Text(ui.Loc["phone.hint"], 13, muted: true));

        // the code sits on white with its own quiet zone so every phone camera reads it, in light and dark themes
        var qr = new Image
        {
            Source = LoadPng(PhoneShareLink.CreateQrPng(url, 360)),
            Width = 260,
            Height = 260,
            Stretch = Stretch.Uniform,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        root.Children.Add(new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 12),
            Child = qr,
        });

        var link = new TextBox
        {
            Text = url,
            IsReadOnly = true,
            MinHeight = 32,
            Padding = new Thickness(6, 5, 6, 5),
            FlowDirection = FlowDirection.LeftToRight, // a URL is always left to right
            Margin = new Thickness(0, 0, 8, 0),
        };
        link.GotFocus += (_, _) => link.SelectAll();
        var copy = ui.Secondary(ui.Loc["phone.copy"]);
        copy.MinWidth = 90;
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(url); copy.Content = ui.Loc["phone.copied"]; }
            catch (System.Runtime.InteropServices.COMException) { /* clipboard busy: the user can copy the box by hand */ }
        };
        DockPanel.SetDock(copy, Dock.Right);
        root.Children.Add(new DockPanel { Children = { copy, link }, Margin = new Thickness(0, 0, 0, 8) });

        if (server.Mode == ShareBindMode.LoopbackOnly)
        {
            var warning = ui.Text(ui.Loc["phone.localOnly"] + (server.LanUnavailableReason is { } why ? "\n" + why : ""), 12);
            warning.Foreground = Brushes.IndianRed;
            root.Children.Add(warning);
        }

        // files
        root.Children.Add(ui.SectionTitle(ui.Loc["phone.files"]));
        var list = new StackPanel();
        foreach (var file in server.Files)
        {
            var status = ui.Text(ui.Loc["phone.waiting"], 12, muted: true, wrap: false);
            _status[file.Index] = status;
            var row = new StackPanel { Children = { ui.Text(file.Name, 13, weight: FontWeights.SemiBold, wrap: false), status } };
            list.Children.Add(row);
        }
        root.Children.Add(ui.Card(new ScrollViewer { Content = list, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 10));

        _expiry = ui.Text("", 12, muted: true);
        root.Children.Add(_expiry);
        root.Children.Add(ui.Text(ui.Loc["phone.privacy"], 11, muted: true));

        var stop = ui.Primary(ui.Loc["phone.stop"]);
        stop.HorizontalAlignment = HorizontalAlignment.Right;
        stop.Margin = new Thickness(0, 8, 0, 0);
        stop.Click += (_, _) => Close();
        root.Children.Add(stop);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        server.DownloadCompleted += OnCompleted;
        server.Finished += OnFinished;
        _tick.Tick += (_, _) => UpdateExpiry();
        _tick.Start();
        UpdateExpiry();
        Closed += (_, _) =>
        {
            _tick.Stop();
            server.DownloadCompleted -= OnCompleted;
            server.Finished -= OnFinished;
            server.Dispose();
        };
    }

    private static BitmapImage LoadPng(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    // These events come from the server's threads.
    private void OnCompleted(object? sender, ShareDownloadEventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_status.TryGetValue(e.File.Index, out var text)) text.Text = _ui.Loc["phone.downloaded"];
    });

    private void OnFinished(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateExpiry);

    private void UpdateExpiry()
    {
        if (_server.IsFinished || !_server.IsRunning)
        {
            _expiry.Text = _ui.Loc["phone.expired"];
            return;
        }

        _expiry.Text = _server.ExpiresAt is { } at
            ? _ui.Loc.Format("phone.expires", TimeInput.Format(at - DateTimeOffset.UtcNow < TimeSpan.Zero ? TimeSpan.Zero : at - DateTimeOffset.UtcNow))
            : "";
    }
}
