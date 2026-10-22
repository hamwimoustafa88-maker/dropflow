// SPDX-License-Identifier: GPL-3.0-or-later
using DropFlow.Core.Operations;

namespace DropFlow.App.Views.Editors;

/// <summary>Several groups of visual editors (images, media…) behind the one <see cref="IOptionsEditor"/> the runner takes: the first that can edit wins.</summary>
internal sealed class OptionsEditors(params IOptionsEditor[] editors) : IOptionsEditor
{
    public bool CanEdit(string operation, IReadOnlyList<string> files) => editors.Any(e => e.CanEdit(operation, files));

    public Task<IReadOnlyDictionary<string, object?>?> EditAsync(string operation, IReadOnlyList<string> files, IReadOnlyList<ToolOption> options) =>
        editors.First(e => e.CanEdit(operation, files)).EditAsync(operation, files, options);
}
