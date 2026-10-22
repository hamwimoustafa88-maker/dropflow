// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;

namespace DropFlow.Desktop.Platform;

public enum PackageManager { Unknown, Homebrew, Apt, Dnf, Pacman, Zypper }

/// <summary>
/// The command a user types to install a helper program DropFlow does not ship. There is no winget on macOS and Linux and DropFlow
/// never installs software by itself here: it shows the command (Homebrew on macOS, the distribution's package manager on Linux).
/// </summary>
public static class EngineHints
{
    public static PackageManager Current { get; } = Detect();

    private static PackageManager Detect()
    {
        if (OperatingSystem.IsMacOS()) return PackageManager.Homebrew;
        if (!OperatingSystem.IsLinux()) return PackageManager.Unknown;
        try { return File.Exists("/etc/os-release") ? FromOsRelease(File.ReadAllText("/etc/os-release")) : PackageManager.Unknown; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PackageManager.Unknown; }
    }

    /// <summary>Reads <c>ID</c> and <c>ID_LIKE</c> from <c>/etc/os-release</c>.</summary>
    public static PackageManager FromOsRelease(string content)
    {
        var ids = new List<string>();
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("ID=", StringComparison.Ordinal) && !line.StartsWith("ID_LIKE=", StringComparison.Ordinal)) continue;
            var value = line[(line.IndexOf('=') + 1)..].Trim().Trim('"', '\'');
            ids.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var id in ids)
        {
            switch (id.ToLowerInvariant())
            {
                case "debian" or "ubuntu" or "linuxmint" or "pop" or "elementary" or "zorin" or "kali" or "raspbian": return PackageManager.Apt;
                case "fedora" or "rhel" or "centos" or "rocky" or "almalinux" or "nobara": return PackageManager.Dnf;
                case "arch" or "manjaro" or "endeavouros" or "cachyos": return PackageManager.Pacman;
                case "opensuse" or "suse" or "sles" or "opensuse-tumbleweed" or "opensuse-leap": return PackageManager.Zypper;
            }
        }
        return PackageManager.Unknown;
    }

    public static string? InstallCommand(EngineId engine, OsKind os, PackageManager pm)
    {
        if (os == OsKind.MacOS) pm = PackageManager.Homebrew;
        var packages = Packages(engine, pm);
        if (packages is null) return null;
        return pm switch
        {
            PackageManager.Homebrew => "brew install " + packages,
            PackageManager.Apt => "sudo apt install " + packages,
            PackageManager.Dnf => "sudo dnf install " + packages,
            PackageManager.Pacman => "sudo pacman -S " + packages,
            PackageManager.Zypper => "sudo zypper install " + packages,
            _ => null,
        };
    }

    private static string? Packages(EngineId engine, PackageManager pm) => (engine, pm) switch
    {
        (_, PackageManager.Unknown) => null,
        (EngineId.Ffmpeg or EngineId.Ffprobe, _) => "ffmpeg",
        (EngineId.Qpdf, _) => "qpdf",
        (EngineId.Pandoc, _) => "pandoc",
        (EngineId.Tesseract, PackageManager.Homebrew) => "tesseract tesseract-lang",
        (EngineId.Tesseract, PackageManager.Apt) => "tesseract-ocr tesseract-ocr-ara",
        (EngineId.Tesseract, PackageManager.Dnf) => "tesseract tesseract-langpack-ara",
        (EngineId.Tesseract, PackageManager.Pacman) => "tesseract tesseract-data-ara",
        (EngineId.Tesseract, PackageManager.Zypper) => "tesseract-ocr tesseract-ocr-traineddata-arabic",
        (EngineId.LibreOffice, PackageManager.Homebrew) => "--cask libreoffice",
        (EngineId.LibreOffice, _) => "libreoffice",
        (EngineId.SevenZip, PackageManager.Homebrew) => "sevenzip",
        (EngineId.SevenZip, PackageManager.Apt) => "7zip",
        (EngineId.SevenZip, PackageManager.Pacman) => "7zip",
        (EngineId.SevenZip, _) => "7zip",
        _ => null,
    };
}
