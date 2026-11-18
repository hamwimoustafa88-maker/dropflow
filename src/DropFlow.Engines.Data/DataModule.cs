// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;

namespace DropFlow.Engines.Data;

/// <summary>
/// Entry point the apps call to get the data-format converters: JSON, JSON Lines, XML, YAML, CSV, TSV, vCard and iCalendar through one
/// tree (<see cref="DataConverter"/>), and tables to contacts or calendars (<see cref="CardsConverter"/>). Pure in-process work: no
/// engine is needed, so <paramref name="engines"/> is accepted only to match the other modules.
/// </summary>
public static class DataModule
{
    public static IReadOnlyList<IConverter> Create(IEngineProvider engines) =>
    [
        new DataConverter(),
        new CardsConverter(),
    ];
}
