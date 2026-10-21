using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DropFlow.Core.Sources;

namespace DropFlow.App.Views;

/// <summary>
/// "Open from link": a box for a web address, with validation, a progress bar and a cancel button. Enter starts the download, Esc cancels
/// it (or closes the window when nothing is running). The download itself is supplied by the host, so this window stays UI only.
/// </summary>
internal sealed class OpenLinkWindow : Window
{
    private readonly UiKit _ui;
    private readonly Func<Uri, IProgress<DownloadProgress>, CancellationToken, Task<string>> _download;
    private readonly Action<string> _opened;
    private readonly TextBox _url;
    private readonly TextBlock _message;
    private readonly ProgressBar _bar;
    private readonly Button _start;
    private readonly Button _cancel;
    private CancellationTokenSource? _cts;

    /// <param name="download">Downloads the address and returns the saved file's path; throws <see cref="UrlDownloadException"/> or cancellation.</param>
    /// <param name="opened">Called (after this window has closed) with the downloaded file.</param>
    /// <param name="folderShown">The folder files are saved to, mentioned in the privacy line.</param>
    public OpenLinkWindow(
        UiKit ui, Func<Uri, IProgress<DownloadProgress>, CancellationToken, Task<string>> download, Action<string> opened,
        string folderShown, string? initialUrl = null)
    {
        _ui = ui;
        _download = download;
        _opened = opened;
        ui.Style(this, 460, ui.Loc["link.title"]);
        SizeToContent = SizeToContent.Height;

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(ui.Text(ui.Loc["link.title"], 18, weight: FontWeights.SemiBold));
        root.Children.Add(ui.Text(ui.Loc["link.hint"], 13, muted: true));

        _url = new TextBox
        {
            Text = initialUrl ?? "",
            MinHeight = 34,
            Padding = new Thickness(6, 6, 6, 6),
            Margin = new Thickness(0, 10, 0, 6),
            FlowDirection = FlowDirection.LeftToRight, // an address always reads left to right
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = UrlDownloader.MaxUrlLength,
        };
        root.Children.Add(_url);

        _message = ui.Text("", 12);
        _url.TextChanged += (_, _) => _message.Text = ""; // typing again clears the last error
        _message.Foreground = Brushes.IndianRed;
        _message.MinHeight = 18;
        root.Children.Add(_message);

        _bar = new ProgressBar
        {
            Height = 4, Minimum = 0, Maximum = 1, Visibility = Visibility.Collapsed,
            Foreground = ui.Theme.Accent, Background = ui.Theme.Wedge, BorderThickness = new Thickness(0), Margin = new Thickness(0, 2, 0, 8),
        };
        root.Children.Add(_bar);

        root.Children.Add(ui.Text(ui.Loc.Format("link.privacy", folderShown), 11, muted: true));

        _start = ui.Primary(ui.Loc["link.download"]);
        _start.IsDefault = true;
        _start.Margin = new Thickness(8, 8, 0, 0);
        _start.Click += async (_, _) => await StartAsync();
        _cancel = ui.Secondary(ui.Loc["dialog.cancel"]);
        _cancel.Margin = new Thickness(0, 8, 0, 0);
        _cancel.Click += (_, _) => CancelOrClose();
        root.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            FlowDirection = ui.Flow,
            Children = { _start, _cancel },
        });

        Content = root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            CancelOrClose();
        };
        Closing += (_, _) => _cts?.Cancel();
        Loaded += (_, _) => { _url.Focus(); _url.SelectAll(); };
    }

    internal string UrlText { get => _url.Text; set => _url.Text = value; }
    internal string MessageText => _message.Text;
    internal bool IsDownloading => _cts is not null;

    /// <summary>Validates the box and, if it holds a good address, downloads it. Errors are shown in the window, never thrown.</summary>
    internal async Task StartAsync()
    {
        if (_cts is not null) return;
        if (!UrlDownloader.TryValidate(_url.Text, out var uri, out var invalid))
        {
            ShowError(invalid!.Describe(_ui.Loc));
            _url.Focus();
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true);
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (_cts is null) return;
            _bar.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } f) _bar.Value = f;
            _message.Foreground = _ui.Theme.TextMuted;
            _message.Text = p.Describe(_ui.Loc);
        });

        string? path = null;
        try
        {
            path = await _download(uri!, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded) ShowStatus(_ui.Loc["link.cancelled"]);
        }
        catch (UrlDownloadException ex)
        {
            if (IsLoaded) ShowError(ex.Describe(_ui.Loc));
        }
        finally
        {
            _cts = null;
            if (IsLoaded) SetBusy(false);
        }

        if (path is null || !IsLoaded) return;
        Close();
        _opened(path);
    }

    internal void CancelOrClose()
    {
        if (_cts is not null) _cts.Cancel();
        else Close();
    }

    private void SetBusy(bool busy)
    {
        _url.IsEnabled = !busy;
        _start.IsEnabled = !busy;
        _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = busy;
        if (busy) ShowStatus(_ui.Loc["link.downloading"]);
        else _url.Focus();
    }

    private void ShowError(string text)
    {
        _message.Foreground = Brushes.IndianRed;
        _message.Text = text;
    }

    private void ShowStatus(string text)
    {
        _message.Foreground = _ui.Theme.TextMuted;
        _message.Text = text;
    }
}
