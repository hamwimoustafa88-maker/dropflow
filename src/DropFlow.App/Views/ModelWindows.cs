using System.Windows;
using System.Windows.Controls;
using DropFlow.AI.Models;

namespace DropFlow.App.Views;

/// <summary>"Downloading Whisper small…" with a real progress bar and a Cancel button (the partial file is kept, so a retry resumes).</summary>
internal sealed class ModelDownloadWindow : Window
{
    private readonly UiKit _ui;
    private readonly TextBlock _line;
    private readonly ProgressBar _bar;
    private readonly CancellationTokenSource _cts = new();

    public ModelDownloadWindow(UiKit ui, ModelInfo model)
    {
        _ui = ui;
        ui.Style(this, 440, ui.Loc.Format("model.downloading", model.DisplayName));
        Topmost = true;

        _line = ui.Text("", 11, muted: true);
        _bar = new ProgressBar { Height = 4, Maximum = 1, Margin = new Thickness(0, 6, 0, 10), Foreground = ui.Theme.Accent, Background = ui.Theme.Wedge, BorderThickness = new Thickness(0) };
        var cancel = ui.Secondary(ui.Loc["dialog.cancel"]);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Click += (_, _) => _cts.Cancel();

        var stack = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        stack.Children.Add(ui.Text(Title, 16, weight: FontWeights.SemiBold));
        stack.Children.Add(_bar);
        stack.Children.Add(_line);
        stack.Children.Add(cancel);
        Content = stack;
        Closing += (_, _) => _cts.Cancel();
    }

    public CancellationToken Token => _cts.Token;

    public void Report(ModelDownloadProgress p)
    {
        if (p.Phase == ModelDownloadPhase.Verifying)
        {
            _bar.IsIndeterminate = true;
            _line.Text = _ui.Loc["model.verifying"];
            return;
        }

        _bar.IsIndeterminate = false;
        _bar.Value = p.Fraction;
        _line.Text = _ui.Loc.Format("model.progress", $"{p.BytesReceived / 1_048_576.0:0}", $"{p.TotalBytes / 1_048_576.0:0}");
    }
}
