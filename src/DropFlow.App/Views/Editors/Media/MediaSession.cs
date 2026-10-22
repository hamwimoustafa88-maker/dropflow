// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DropFlow.App.Services;
using DropFlow.Core.Operations;
using DropFlow.Engines.Media.Encoding;
using DropFlow.Engines.Media.Preview;

namespace DropFlow.App.Views.Editors.Media;

/// <summary>
/// Playback for one file inside a media editor: the Windows player (<see cref="MediaElement"/>, Media Foundation) when it can open the
/// file, with scrubbing, range playback, speed and frame steps; when it cannot (MKV/WebM without codecs, Ogg, Opus...) video falls back
/// to FFmpeg stills at the playhead and audio to a playable AAC copy made in the scratch folder. Also loads the timeline strip
/// (thumbnails or waveform) in the background. UI-thread affine: every event is raised on the dispatcher.
/// </summary>
internal sealed class MediaSession : IDisposable
{
    private readonly IEngineProvider _engines;
    private readonly string _scratch;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _stillDelay = new() { Interval = TimeSpan.FromMilliseconds(110) };
    private readonly DispatcherTimer _openTimeout = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly CancellationTokenSource _life = new();
    private double? _stopAt;
    private bool _triedCopy;
    private bool _disposed;
    private int _stillVersion;

    public MediaSession(IEngineProvider engines, string path, MediaInfo info, string scratchFolder, bool startPlayer = true)
    {
        _engines = engines;
        _scratch = scratchFolder;
        Path = path;
        Info = info;
        Duration = Math.Max(0.04, info.DurationSeconds ?? 0);
        HasVideo = info.HasVideo;
        FrameStep = 1 / Math.Max(1, info.FrameRate is > 0 ? info.FrameRate.Value : 25);
        DisplayWidth = info.Video?.DisplayWidth ?? 0;
        DisplayHeight = info.Video?.DisplayHeight ?? 0;

        Player = new MediaElement
        {
            LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true,
            Stretch = Stretch.Uniform, Volume = 0.9, IsMuted = false,
        };
        Still = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(Still, BitmapScalingMode.HighQuality);

        _tick.Tick += (_, _) => OnTick();
        _stillDelay.Tick += async (_, _) => { _stillDelay.Stop(); await ShowStillAsync(Current); };
        _openTimeout.Tick += (_, _) => { _openTimeout.Stop(); if (!IsPlayerReady) OnPlayerFailed(); };
        Player.MediaOpened += (_, _) => OnOpened();
        Player.MediaFailed += (_, e) => { FileLog.Info($"media player could not open {System.IO.Path.GetFileName(Path)}: {e.ErrorException?.Message}"); OnPlayerFailed(); };
        Player.MediaEnded += (_, _) => { Pause(); Seek(Duration); };

        if (startPlayer)
        {
            Player.Source = new Uri(path);
            _openTimeout.Start();
        }
        if (HasVideo) _ = ShowStillAsync(0);
    }

    /// <summary>Probes the file and creates a session. Throws <see cref="MediaProbeException"/> when the file cannot be read.</summary>
    public static async Task<MediaSession> OpenAsync(IEngineProvider engines, string path, string scratchFolder, CancellationToken cancellationToken = default)
    {
        var info = await MediaPreview.ProbeAsync(engines, path, cancellationToken);
        if (info.DurationSeconds is not > 0) throw new MediaProbeException("The length of the file could not be read.");
        return new MediaSession(engines, path, info, scratchFolder);
    }

    public string Path { get; }
    public MediaInfo Info { get; }
    public double Duration { get; }
    public bool HasVideo { get; }
    public double FrameStep { get; }
    public int DisplayWidth { get; }
    public int DisplayHeight { get; }
    public IEngineProvider Engines => _engines;
    public string Scratch => _scratch;

    public MediaElement Player { get; }

    /// <summary>The picture shown until the player has a frame, and at every seek when the player cannot open the file.</summary>
    public Image Still { get; }

    public double Current { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool IsPlayerReady { get; private set; }

    /// <summary>True when the Windows player cannot play this file, so video shows FFmpeg stills and cannot play.</summary>
    public bool UsesStills { get; private set; }

    public bool CanPlay => IsPlayerReady;

    public double Speed
    {
        get => Player.SpeedRatio;
        set => Player.SpeedRatio = Math.Clamp(value, 0.25, 4);
    }

    /// <summary>Thumbnails for the strip (null until loaded), or empty for audio.</summary>
    public BitmapSource?[] Thumbnails { get; private set; } = [];

    /// <summary>Waveform peaks 0..1 (empty until loaded).</summary>
    public float[] Waveform { get; private set; } = [];

    /// <summary>The time, play state or player state changed.</summary>
    public event Action? Changed;

    /// <summary>Thumbnails or the waveform arrived (or more of them).</summary>
    public event Action? StripChanged;

    // ------------------------------------------------------------------------------------------------------------------ strip

    /// <summary>Starts loading the timeline strip: <paramref name="thumbnails"/> frames for video, <paramref name="bars"/> peaks for audio.</summary>
    public void LoadStrip(int thumbnails = 12, int bars = 600, int thumbHeight = 72)
    {
        var ct = _life.Token;
        var ui = Dispatcher.CurrentDispatcher;
        if (HasVideo)
        {
            Thumbnails = new BitmapSource?[thumbnails];
            _ = MediaPreview.ThumbnailsAsync(_engines, Path, Duration, thumbnails, thumbHeight, (i, png) =>
            {
                var image = Decode(png);
                if (image is null) return;
                ui.BeginInvoke(() => { if (_disposed) return; Thumbnails[i] = image; StripChanged?.Invoke(); });
            }, ct).ContinueWith(t => Log(t, "thumbnails"), TaskScheduler.Default);
        }
        if (Info.HasAudio)
        {
            _ = Task.Run(async () =>
            {
                var peaks = await MediaPreview.WaveformAsync(_engines, Path, bars, ct);
                await ui.InvokeAsync(() => { if (_disposed) return; Waveform = peaks; StripChanged?.Invoke(); });
            }, ct).ContinueWith(t => Log(t, "waveform"), TaskScheduler.Default);
        }
    }

    private static void Log(Task t, string what)
    {
        if (t.IsFaulted) FileLog.Info($"media editor {what}: {t.Exception?.GetBaseException().Message}");
    }

    internal static BitmapSource? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>A still at <paramref name="seconds"/> (for frame lists and previews); null when FFmpeg cannot make one.</summary>
    public async Task<BitmapSource?> FrameAsync(double seconds, int height)
    {
        var t = Math.Clamp(seconds, 0, Math.Max(0, Duration - FrameStep));
        var png = await MediaPreview.FrameAsync(_engines, Path, t, height, _life.Token);
        return png is null ? null : Decode(png);
    }

    /// <summary>
    /// Shows the exact frame at <paramref name="seconds"/> made by FFmpeg. While paused this is what the user looks at (the player's own
    /// paused frame is not reliable for every file and is not captured by screenshots); during playback the player shows the video.
    /// </summary>
    private async Task ShowStillAsync(double seconds)
    {
        if (!HasVideo || _disposed) return;
        var version = ++_stillVersion;
        try
        {
            var height = Math.Clamp(DisplayHeight > 0 ? DisplayHeight : 540, 120, 720);
            var frame = await FrameAsync(seconds, height);
            if (frame is null) return;
            await Still.Dispatcher.InvokeAsync(() =>
            {
                if (version != _stillVersion || _disposed || IsPlaying) return;
                Still.Source = frame;
                Still.Visibility = Visibility.Visible;
            });
        }
        catch (OperationCanceledException) { /* window closed */ }
    }

    // ------------------------------------------------------------------------------------------------------------------ player

    private void OnOpened()
    {
        _openTimeout.Stop();
        IsPlayerReady = true;
        // Windows opened the file but has no decoder for its picture (HEVC without the extension, say): sound plays, the picture
        // comes from FFmpeg stills.
        if (HasVideo && !Player.HasVideo) UsesStills = true;
        // A play + pause makes the player draw its first frame; scrubbing then shows the frame at every seek.
        Player.Play();
        Player.Pause();
        Player.Position = TimeSpan.FromSeconds(Current);
        Changed?.Invoke();
    }

    private void OnPlayerFailed()
    {
        if (_disposed) return;
        _openTimeout.Stop();
        IsPlayerReady = false;
        IsPlaying = false;
        _tick.Stop();
        Player.Close();
        if (HasVideo)
        {
            UsesStills = true;
            Still.Visibility = Visibility.Visible;
            Changed?.Invoke();
            return;
        }
        if (!_triedCopy)
        {
            // Audio the Windows player cannot open: make a playable copy and play that instead.
            _triedCopy = true;
            _ = PlayCopyAsync();
        }
        Changed?.Invoke();
    }

    private async Task PlayCopyAsync()
    {
        try
        {
            var copy = await MediaPreview.PlayableCopyAsync(_engines, Path, _scratch, _life.Token);
            if (_disposed) return;
            Player.Source = new Uri(copy);
            _openTimeout.Start();
        }
        catch (Exception ex) when (ex is MediaPreviewException or OperationCanceledException or IOException)
        {
            FileLog.Info("media editor playable copy: " + ex.Message);
        }
    }

    private void OnTick()
    {
        if (!IsPlayerReady) return;
        Current = Math.Min(Player.Position.TotalSeconds, Duration);
        if (_stopAt is { } stop && Current >= stop)
        {
            Pause();
            Seek(stop);
        }
        else if (UsesStills && DateTime.UtcNow - _lastPlayingStill > TimeSpan.FromMilliseconds(400))
        {
            // The picture cannot play here: refresh the still a few times a second while the sound plays.
            _lastPlayingStill = DateTime.UtcNow;
            _ = ShowPlayingStillAsync(Current);
        }
        Changed?.Invoke();
    }

    private DateTime _lastPlayingStill;

    private async Task ShowPlayingStillAsync(double seconds)
    {
        try
        {
            var frame = await FrameAsync(seconds, Math.Clamp(DisplayHeight > 0 ? DisplayHeight : 360, 120, 480));
            if (frame is not null) await Still.Dispatcher.InvokeAsync(() => { if (!_disposed) Still.Source = frame; });
        }
        catch (OperationCanceledException) { /* closed */ }
    }

    public void Seek(double seconds)
    {
        Current = Math.Clamp(seconds, 0, Duration);
        if (IsPlayerReady)
        {
            Player.Position = TimeSpan.FromSeconds(Current);
            // While scrubbing the player's own frame follows the pointer; once the pointer rests, the exact still takes over.
            if (HasVideo && !IsPlaying && !UsesStills) Still.Visibility = Visibility.Collapsed;
        }
        if (HasVideo && !IsPlaying) { _stillDelay.Stop(); _stillDelay.Start(); }
        Changed?.Invoke();
    }

    /// <summary>Moves one frame (or <paramref name="frames"/> frames) and pauses.</summary>
    public void Step(int frames)
    {
        Pause();
        Seek(Current + frames * FrameStep);
    }

    public void Pause()
    {
        var wasPlaying = IsPlaying;
        if (IsPlayerReady) Player.Pause();
        _tick.Stop();
        IsPlaying = false;
        _stopAt = null;
        if (wasPlaying && HasVideo) { _stillDelay.Stop(); _stillDelay.Start(); }
        Changed?.Invoke();
    }

    /// <summary>Plays from <paramref name="from"/> (or the playhead) and stops at <paramref name="to"/>; pauses when already playing.</summary>
    public void TogglePlay(double? from = null, double? to = null)
    {
        if (!IsPlayerReady) return;
        if (IsPlaying) { Pause(); return; }
        if (from is { } f) Seek(f);
        else if (Current >= Duration - 0.05) Seek(0);
        _stopAt = to;
        _stillDelay.Stop();
        _stillVersion++;   // a still that is still on its way must not cover the playing video
        Player.Play();
        if (HasVideo && !UsesStills) Still.Visibility = Visibility.Collapsed;
        IsPlaying = true;
        _tick.Start();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _life.Cancel();
        _tick.Stop();
        _stillDelay.Stop();
        _openTimeout.Stop();
        try
        {
            Player.Stop();
            Player.Close();
        }
        catch (InvalidOperationException) { /* never opened */ }
    }
}

/// <summary>Plays the short listening renders (bleep A/B, one side of a stereo file). One at a time; a second press of the same key stops it.</summary>
internal sealed class MediaPreviewPlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private CancellationTokenSource? _pending;

    public MediaPreviewPlayer()
    {
        _player.MediaEnded += (_, _) => Stop();
        _player.MediaFailed += (_, e) => { FileLog.Info("preview playback: " + e.ErrorException?.Message); Failed?.Invoke(); Stop(); };
    }

    /// <summary>The key of what is playing or being rendered, or null.</summary>
    public string? Playing { get; private set; }

    public event Action? Changed;
    public event Action? Failed;

    public async Task ToggleAsync(string key, Func<CancellationToken, Task<string>> render)
    {
        if (Playing == key) { Stop(); return; }
        Stop();
        Playing = key;
        Changed?.Invoke();
        var cts = _pending = new CancellationTokenSource();
        try
        {
            var file = await render(cts.Token);
            if (cts.IsCancellationRequested || Playing != key) return;
            _player.Open(new Uri(file));
            _player.Play();
        }
        catch (OperationCanceledException) { /* replaced or closed */ }
        catch (Exception ex) when (ex is MediaPreviewException or IOException)
        {
            FileLog.Info("preview render: " + ex.Message);
            Playing = null;
            Failed?.Invoke();
            Changed?.Invoke();
        }
    }

    public void Stop()
    {
        _pending?.Cancel();
        _player.Stop();
        _player.Close();
        if (Playing is null) return;
        Playing = null;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();
}
