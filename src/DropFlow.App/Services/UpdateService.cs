using Velopack;
using Velopack.Sources;

namespace DropFlow.App.Services;

public enum UpdateState { NotInstalled, UpToDate, Available, Failed }

public sealed record UpdateCheck(UpdateState State, string? Version = null, string? Error = null);

/// <summary>
/// Auto-update through Velopack and GitHub Releases. Only active for an installed copy (a Velopack install); a dev build or a
/// portable copy reports <see cref="UpdateState.NotInstalled"/>. Every failure is swallowed into a result — updates must never crash the app.
/// </summary>
internal sealed class UpdateService(string repoUrl)
{
    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private int _installing;

    private UpdateManager Manager => _manager ??= new UpdateManager(new GithubSource(repoUrl, null, false));

    public async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            if (!Manager.IsInstalled) return new UpdateCheck(UpdateState.NotInstalled);
            _pending = await Manager.CheckForUpdatesAsync();
            return _pending is null
                ? new UpdateCheck(UpdateState.UpToDate)
                : new UpdateCheck(UpdateState.Available, _pending.TargetFullRelease.Version.ToString());
        }
        catch (Exception ex)
        {
            FileLog.Error("update check", ex);
            return new UpdateCheck(UpdateState.Failed, Error: ex.Message);
        }
    }

    /// <summary>
    /// Downloads the pending update and restarts into it. True: restarting; false: nothing to install or it failed; null: an install is
    /// already running (a second press of "Update and restart" used to start a second download, which then failed on Velopack's lock).
    /// </summary>
    public async Task<bool?> InstallAsync(Action<int>? progress = null)
    {
        if (Interlocked.Exchange(ref _installing, 1) == 1) return null;
        try
        {
            if (_pending is null) return false;
            await Manager.DownloadUpdatesAsync(_pending, progress);
            Manager.ApplyUpdatesAndRestart(_pending);
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Error("update install", ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _installing, 0);
        }
    }
}
