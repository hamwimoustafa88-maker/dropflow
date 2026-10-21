using DropFlow.Core.Operations;
using DropFlow.Engines.Archive.Archives;
using DropFlow.Engines.Archive.General;
using DropFlow.Engines.Archive.General.Rename;
using DropFlow.Engines.Archive.Images;

namespace DropFlow.Engines.Archive;

/// <summary>
/// Entry point the app calls to get every converter this project provides: archives (arc.*, convert:zip|tar|gz|7z),
/// utilities (gen.hash, gen.base64, gen.rename) and the image tools img.qr and img.icons. The phone-share back-end
/// (<c>DropFlow.Engines.Archive.Sharing.PhoneShareServer</c>) is not a converter and is used directly by the app.
/// </summary>
public static class ArchiveModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) =>
    [
        new ArchiveConverter(engines),
        new HashConverter(),
        new Base64Converter(),
        new RenameConverter(),
        new QrConverter(),
        new IconsConverter(),
    ];
}
