// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Fonts;

/// <summary>
/// Entry point the app calls: font conversions between TTF, OTF, WOFF, WOFF2 and EOT (output). Everything runs in-process from the
/// public specifications; no engine is needed, so <paramref name="engines"/> is not used today.
/// </summary>
public static class FontsModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) => [new FontConverter()];
}
