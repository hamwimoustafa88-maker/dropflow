// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DropFlow.App.Services;
using DropFlow.Engines.Archive.Images;
using ImageMagick;

namespace DropFlow.App.Views.Editors;

/// <summary>
/// QR codes (img.qr). Create: text, link or Wi-Fi; module and eye shapes, gradient, eye colour, transparent background, a logo on a
/// plate (error correction then goes to H automatically), PNG and/or SVG. The preview is rendered by the engine's
/// <see cref="QrDesigner"/> and decoded again with ZXing on every change, so "scans correctly" is a real check, not a guess.
/// Read: when the dropped file is a picture, the code inside it is shown and can be saved as text.
/// </summary>
internal sealed class QrWindow : VisualEditorWindow
{
    private sealed record Rendered(BitmapSource? Bitmap, bool Scans, string? Error);

    private static readonly string[] ImageExtensions = ["jpg", "jpeg", "png", "webp", "heic", "tiff", "tif", "avif", "bmp", "gif", "svg", "ico"];

    private readonly PreviewScheduler<Rendered> _renders = new();
    private readonly StackPanel _create = new();
    private readonly StackPanel _read = new();
    private readonly StackPanel _textPanel = new();
    private readonly StackPanel _linkPanel = new();
    private readonly StackPanel _wifiPanel = new();
    private readonly TextBox _text = new() { MinHeight = 70, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox _link = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBox _ssid = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4) };
    private readonly TextBox _password = new() { MinHeight = 30, Padding = new Thickness(6, 4, 6, 4), FlowDirection = FlowDirection.LeftToRight };
    private readonly TextBlock _status;
    private readonly TextBlock _decoded;
    private readonly TextBlock _logoName;
    private readonly StackPanel _color2Panel = new();
    private readonly StackPanel _eyePanel = new();
    private readonly bool _inputIsImage;
    private string _mode = "create";
    private string _content = "text";
    private string _security = "WPA";
    private bool _hidden;
    private QrModuleShape _modules = QrModuleShape.Rounded;
    private QrEyeShape _eyes = QrEyeShape.Rounded;
    private string _color = "#0a0f24";
    private string? _color2 = null;
    private string _color2Value = "#4338ca";
    private double _angle = 45;
    private string? _eyeColor = null;
    private string _eyeColorValue = "#4338ca";
    private string _background = "#ffffff";
    private string? _logo;
    private bool _plate = true;
    private string _level = "auto";
    private int _size = 1024;
    private string _format = "png";
    private string? _readText;
    private bool _lastScans;

    public QrWindow(UiKit ui, IReadOnlyList<string> files) : base(ui, ui.Loc["editor.qr.title"], files)
    {
        var ext = files.Count > 0 ? Path.GetExtension(files[0]).TrimStart('.').ToLowerInvariant() : "";
        _inputIsImage = ImageExtensions.Contains(ext);

        if (_inputIsImage)
        {
            var modes = new[] { ("create", "editor.qr.mode.create"), ("read", "editor.qr.mode.read") }.Select(m => (ui.Loc[m.Item2], m.Item1));
            Side.Children.Add(EditorControls.Segmented(ui, modes, _mode, m => { _mode = m; ShowMode(); Render(); }).Panel);
        }

        // ---- read
        _decoded = ui.Text(ui.Loc["editor.qr.reading"], 13);
        _decoded.FlowDirection = FlowDirection.LeftToRight;
        _read.Children.Add(EditorControls.Label(ui, ui.Loc["editor.qr.found"]));
        _read.Children.Add(ui.Card(_decoded, 10));
        Side.Children.Add(_read);

        // ---- create: content
        _create.Children.Add(EditorControls.Label(ui, ui.Loc["editor.qr.content"]));
        var contents = new[] { "text", "link", "wifi" }.Select(c => (ui.Loc["editor.qr.content." + c], c));
        _create.Children.Add(EditorControls.Segmented(ui, contents, _content, c => { _content = c; ShowContent(); Render(); }).Panel);

        _textPanel.Children.Add(_text);
        _linkPanel.Children.Add(_link);
        _wifiPanel.Children.Add(EditorControls.Label(ui, ui.Loc["editor.qr.wifi.ssid"]));
        _wifiPanel.Children.Add(_ssid);
        _wifiPanel.Children.Add(EditorControls.Label(ui, ui.Loc["editor.qr.wifi.password"]));
        _wifiPanel.Children.Add(_password);
        var security = new[] { ("WPA", "editor.qr.wifi.wpa"), ("WEP", "editor.qr.wifi.wep"), ("nopass", "editor.qr.wifi.open") }.Select(s => (ui.Loc[s.Item2], s.Item1));
        _wifiPanel.Children.Add(EditorControls.Label(ui, ui.Loc["editor.qr.wifi.security"]));
        _wifiPanel.Children.Add(EditorControls.Segmented(ui, security, _security, s => { _security = s; Render(); }).Panel);
        _wifiPanel.Children.Add(EditorControls.Check(ui, ui.Loc["editor.qr.wifi.hidden"], _hidden, v => { _hidden = v; Render(); }));
        foreach (var p in new[] { _textPanel, _linkPanel, _wifiPanel }) _create.Children.Add(p);
        foreach (var box in new[] { _text, _link, _ssid, _password }) box.TextChanged += (_, _) => Render();

        // ---- create: shapes
        var shapes = new StackPanel();
        shapes.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.modules"]));
        shapes.Children.Add(EditorControls.Segmented(ui, Enum.GetValues<QrModuleShape>().Select(m => (ui.Loc["opt.img.qr.modules." + m.ToString().ToLowerInvariant()], m)), _modules, m => { _modules = m; Render(); }).Panel);
        shapes.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.eyes"]));
        shapes.Children.Add(EditorControls.Segmented(ui, Enum.GetValues<QrEyeShape>().Select(m => (ui.Loc["opt.img.qr.eyes." + m.ToString().ToLowerInvariant()], m)), _eyes, m => { _eyes = m; Render(); }).Panel);
        _create.Children.Add(EditorControls.Group(ui, ui.Loc["editor.qr.shapes"], shapes));

        // ---- create: colours
        var colours = new StackPanel();
        colours.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.color"]));
        colours.Children.Add(EditorControls.ColorPicker(ui, _color, c => { _color = c; Render(); }).Panel);
        colours.Children.Add(EditorControls.Check(ui, ui.Loc["editor.qr.gradient"], false, v => { _color2 = v ? _color2Value : null; _color2Panel.Visibility = v ? Visibility.Visible : Visibility.Collapsed; Render(); }));
        _color2Panel.Children.Add(EditorControls.ColorPicker(ui, _color2Value, c => { _color2Value = c; _color2 = c; Render(); }).Panel);
        _color2Panel.Children.Add(EditorControls.SliderRow(ui, ui.Loc["editor.background.angle"], 0, 360, _angle, 45, v => { _angle = Math.Round(v); Render(); }).Row);
        _color2Panel.Visibility = Visibility.Collapsed;
        colours.Children.Add(_color2Panel);
        colours.Children.Add(EditorControls.Check(ui, ui.Loc["editor.qr.eyeColor"], false, v => { _eyeColor = v ? _eyeColorValue : null; _eyePanel.Visibility = v ? Visibility.Visible : Visibility.Collapsed; Render(); }));
        _eyePanel.Children.Add(EditorControls.ColorPicker(ui, _eyeColorValue, c => { _eyeColorValue = c; _eyeColor = c; Render(); }).Panel);
        _eyePanel.Visibility = Visibility.Collapsed;
        colours.Children.Add(_eyePanel);
        colours.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.background"]));
        colours.Children.Add(EditorControls.ColorPicker(ui, _background, c => { _background = c; Render(); }, allowTransparent: true).Panel);
        _create.Children.Add(EditorControls.Group(ui, ui.Loc["editor.qr.colours"], colours, expanded: false));

        // ---- create: logo
        var logo = new StackPanel();
        _logoName = ui.Text(ui.Loc["editor.qr.noLogo"], 12, muted: true, wrap: false);
        _logoName.TextTrimming = TextTrimming.CharacterEllipsis;
        logo.Children.Add(_logoName);
        var logoButtons = new WrapPanel();
        var choose = ui.Secondary(ui.Loc["editor.qr.chooseLogo"]);
        choose.Click += (_, _) => ChooseLogo();
        logoButtons.Children.Add(choose);
        if (_inputIsImage)
        {
            var useInput = ui.Secondary(ui.Loc["editor.qr.useThisPicture"]);
            useInput.Click += (_, _) => SetLogo(Files[0]);
            logoButtons.Children.Add(useInput);
        }
        var remove = ui.Secondary(ui.Loc["editor.qr.removeLogo"]);
        remove.Click += (_, _) => SetLogo(null);
        logoButtons.Children.Add(remove);
        foreach (Button b in logoButtons.Children) { b.MinWidth = 0; b.Margin = new Thickness(0, 4, 6, 4); }
        logo.Children.Add(logoButtons);
        logo.Children.Add(EditorControls.Check(ui, ui.Loc["opt.img.qr.plate"], _plate, v => { _plate = v; Render(); }));
        _create.Children.Add(EditorControls.Group(ui, ui.Loc["opt.img.qr.logo"], logo, expanded: false));

        // ---- create: output
        var output = new StackPanel();
        output.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.errorCorrection"]));
        output.Children.Add(EditorControls.Segmented(ui, new[] { "auto", "L", "M", "Q", "H" }.Select(l => (ui.Loc["opt.img.qr.errorCorrection." + l], l)), _level, l => { _level = l; Render(); }).Panel);
        output.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.size"]));
        output.Children.Add(EditorControls.Segmented(ui, new[] { 512, 1024, 2048 }.Select(s => ($"{s} px", s)), _size, s => _size = s).Panel);
        output.Children.Add(EditorControls.Label(ui, ui.Loc["opt.img.qr.format"]));
        output.Children.Add(EditorControls.Segmented(ui, new[] { "png", "svg", "both" }.Select(f => (ui.Loc["opt.img.qr.format." + f], f)), _format, f => _format = f).Panel);
        _create.Children.Add(EditorControls.Group(ui, ui.Loc["editor.qr.output"], output, expanded: false));

        Side.Children.Add(_create);

        _status = ui.Text("", 13, weight: FontWeights.SemiBold);
        _status.Margin = new Thickness(0, 10, 0, 0);
        Side.Children.Add(_status);

        // a .txt file (or a picture that already holds a code) fills the content
        if (files.Count > 0 && ext == "txt")
        {
            try { _text.Text = ReadText(files[0]); }
            catch (IOException ex) { FileLog.Error("qr: read text", ex); }
        }
        if (string.IsNullOrEmpty(_text.Text)) _text.Text = "https://dropflow.scouthub.dev";

        ShowMode();
        ShowContent();
        _renders.BusyChanged += () => Preview.ShowBusy(_renders.IsBusy);
        Loaded += async (_, _) => { if (_inputIsImage) await ReadInputAsync(); Render(); };
    }

    public override bool IsReady => _renders.Applied > 0 && !_renders.IsBusy;

    internal bool LastScans => _lastScans;
    internal string StatusText => _status.Text;

    internal void SetModules(QrModuleShape shape) { _modules = shape; Render(); }
    internal void SetEyes(QrEyeShape shape) { _eyes = shape; Render(); }
    internal void SetText(string text) { _content = "text"; ShowContent(); _text.Text = text; }
    internal void SetWifi(string ssid, string password) { _content = "wifi"; ShowContent(); _ssid.Text = ssid; _password.Text = password; }
    internal void SetGradient(string? to) { _color2 = to; Render(); }

    private static string ReadText(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[4096];
        var n = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, n).TrimEnd('\r', '\n');
    }

    /// <summary>The text that goes into the code for the current content type ("" when incomplete).</summary>
    internal string ContentText => _content switch
    {
        "link" => string.IsNullOrWhiteSpace(_link.Text) ? "" : QrContent.Link(_link.Text),
        "wifi" => string.IsNullOrWhiteSpace(_ssid.Text) ? "" : QrContent.Wifi(_ssid.Text.Trim(), _password.Text, _security, _hidden),
        _ => QrContent.Text(_text.Text),
    };

    private QrErrorCorrection? Level => _level switch
    {
        "L" => QrErrorCorrection.L, "M" => QrErrorCorrection.M, "Q" => QrErrorCorrection.Q, "H" => QrErrorCorrection.H,
        _ => null,
    };

    private QrStyle DesignStyle => new(_modules, _eyes, _color, _color2, _angle, _eyeColor, _background, _logo, LogoPlate: _plate);

    private void ShowMode()
    {
        _create.Visibility = _mode == "create" ? Visibility.Visible : Visibility.Collapsed;
        _read.Visibility = _mode == "read" ? Visibility.Visible : Visibility.Collapsed;
        _status.Visibility = _mode == "create" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowContent()
    {
        _textPanel.Visibility = _content == "text" ? Visibility.Visible : Visibility.Collapsed;
        _linkPanel.Visibility = _content == "link" ? Visibility.Visible : Visibility.Collapsed;
        _wifiPanel.Visibility = _content == "wifi" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReadInputAsync()
    {
        var path = Files[0];
        try
        {
            var found = await Task.Run(() => QrCodeService.Read(path));
            _readText = found.Count > 0 ? found[0].Text : null;
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("qr: read picture", ex);
            _readText = null;
        }
        _decoded.Text = _readText ?? Ui.Loc["editor.qr.noneFound"];
        if (_readText is not null)
        {
            _mode = "read";
            _text.Text = _readText;
            ShowMode();
        }
    }

    private void ChooseLogo()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Ui.Loc["editor.qr.chooseLogo"],
            Filter = Ui.Loc["editor.pictureFilter"] + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.svg;*.ico",
        };
        if (dialog.ShowDialog(this) == true) SetLogo(dialog.FileName);
    }

    private void SetLogo(string? path)
    {
        _logo = path;
        _logoName.Text = path is null ? Ui.Loc["editor.qr.noLogo"] : Path.GetFileName(path);
        Render();
    }

    private void Render()
    {
        if (_mode == "read")
        {
            if (_inputIsImage && Preview.PixelWidth == 0) _ = ShowInputAsync();
            return;
        }
        var text = ContentText;
        if (string.IsNullOrEmpty(text))
        {
            _status.Text = Ui.Loc["editor.qr.empty"];
            _status.Foreground = Ui.Theme.TextMuted;
            return;
        }
        var design = new QrDesign(text, DesignStyle, Level, 640, 4);
        _ = _renders.RequestAsync(ct =>
        {
            try
            {
                var png = QrDesigner.RenderPng(design);
                ct.ThrowIfCancellationRequested();
                var check = QrDesigner.Check(png, text);
                return new Rendered(PreviewImage.FromPng(png), check.Scans, null);
            }
            catch (QrTooLargeException)
            {
                return new Rendered(null, false, "editor.qr.tooLong");
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or MagickException)
            {
                return new Rendered(null, false, ex is FileNotFoundException ? "editor.qr.logoMissing" : "editor.qr.invalid");
            }
        }, ShowRendered, ex => FileLog.Error("qr preview", ex));
    }

    private async Task ShowInputAsync()
    {
        try
        {
            var path = Files[0];
            using var source = await Task.Run(() => PreviewImage.Load(path));
            Preview.SetImage(PreviewImage.ToBitmap(source.Image));
        }
        catch (Exception ex) when (ex is MagickException or IOException or UnauthorizedAccessException)
        {
            FileLog.Error("qr: show picture", ex);
        }
    }

    private void ShowRendered(Rendered r)
    {
        if (_mode != "create") return;
        if (r.Bitmap is not null) Preview.SetImage(r.Bitmap);
        _lastScans = r.Bitmap is not null && r.Scans;
        if (r.Error is not null)
        {
            _status.Text = Ui.Loc[r.Error];
            _status.Foreground = Brushes.IndianRed;
        }
        else
        {
            _status.Text = Ui.Loc[r.Scans ? "editor.qr.scans" : "editor.qr.mayNotScan"];
            _status.Foreground = r.Scans ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) : Brushes.IndianRed;
        }
    }

    protected override IReadOnlyDictionary<string, object?>? BuildOptions()
    {
        if (_mode == "read")
        {
            if (_readText is null) { Message.Text = Ui.Loc["editor.qr.noneFound"]; return null; }
            return new Dictionary<string, object?> { ["mode"] = "read" };
        }
        var text = ContentText;
        if (string.IsNullOrEmpty(text)) { Message.Text = Ui.Loc["editor.qr.empty"]; return null; }
        return new Dictionary<string, object?>
        {
            ["mode"] = "create",
            ["text"] = text,
            ["errorCorrection"] = _level,
            ["size"] = (double)_size,
            ["quietZone"] = 4d,
            ["modules"] = _modules.ToString().ToLowerInvariant(),
            ["eyes"] = _eyes.ToString().ToLowerInvariant(),
            ["color"] = _color,
            ["color2"] = _color2 ?? "",
            ["angle"] = _angle,
            ["eyeColor"] = _eyeColor ?? "",
            ["background"] = _background,
            ["logo"] = _logo ?? "",
            ["plate"] = _plate,
            ["format"] = _format,
        };
    }

    protected override void OnClosedCleanup() => _renders.Cancel();
}
