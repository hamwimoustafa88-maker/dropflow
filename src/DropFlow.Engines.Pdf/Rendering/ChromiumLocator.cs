namespace DropFlow.Engines.Pdf.Rendering;

/// <summary>
/// Finds an installed Chromium-family browser (Google Chrome, Chromium, Microsoft Edge, Brave) that can print a page to PDF headlessly.
/// The environment variable <c>DROPFLOW_CHROME</c> names one explicitly. Nothing is ever downloaded.
/// </summary>
public sealed class ChromiumLocator(Func<string, bool>? fileExists = null, IEnumerable<string>? pathDirs = null, bool? windows = null, bool? macOs = null)
{
    public const string EnvironmentVariable = "DROPFLOW_CHROME";

    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;
    private readonly IReadOnlyList<string> _pathDirs = (pathDirs ?? (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
    private readonly bool _windows = windows ?? OperatingSystem.IsWindows();
    private readonly bool _macOs = macOs ?? OperatingSystem.IsMacOS();

    /// <summary>Executable names looked up on PATH (Linux packages, Homebrew links), in order of preference.</summary>
    public static readonly IReadOnlyList<string> UnixNames =
        ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable", "brave-browser"];

    /// <summary>The browser to use, or null when none is installed.</summary>
    public string? Locate()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } explicitPath && _fileExists(explicitPath)) return explicitPath;
        return Candidates().FirstOrDefault(_fileExists);
    }

    /// <summary>Every place a browser is looked for, in order.</summary>
    public IEnumerable<string> Candidates()
    {
        if (_windows)
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     }.Where(r => r.Length > 0))
            {
                yield return Path.Combine(root, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(root, "Chromium", "Application", "chrome.exe");
                yield return Path.Combine(root, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            }
            foreach (var dir in _pathDirs)
                foreach (var name in new[] { "chrome.exe", "msedge.exe" })
                    yield return Path.Combine(dir, name);
            yield break;
        }

        if (_macOs)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var apps in new[] { "/Applications", Path.Combine(home, "Applications") })
            {
                yield return Path.Combine(apps, "Google Chrome.app", "Contents", "MacOS", "Google Chrome");
                yield return Path.Combine(apps, "Chromium.app", "Contents", "MacOS", "Chromium");
                yield return Path.Combine(apps, "Microsoft Edge.app", "Contents", "MacOS", "Microsoft Edge");
                yield return Path.Combine(apps, "Brave Browser.app", "Contents", "MacOS", "Brave Browser");
            }
        }

        foreach (var dir in _pathDirs.Concat(["/usr/bin", "/usr/local/bin", "/opt/homebrew/bin", "/snap/bin"]))
            foreach (var name in UnixNames)
                yield return Path.Combine(dir, name);
    }
}
