using DropFlow.Core.Operations;

namespace DropFlow.Core.Registry;

/// <summary>Maps (operation, input extension) to the converter that implements it.</summary>
public sealed class ToolRegistry
{
    private readonly List<IConverter> _converters = [];

    public IReadOnlyList<IConverter> Converters => _converters;

    public ToolRegistry Register(IConverter converter)
    {
        _converters.Add(converter);
        return this;
    }

    public ToolRegistry Register(IEnumerable<IConverter> converters)
    {
        foreach (var c in converters) Register(c);
        return this;
    }

    public IConverter? Find(string operation, string inputExtension) =>
        _converters.FirstOrDefault(c => c.Supports(operation, inputExtension.TrimStart('.').ToLowerInvariant()));

    public bool Supports(string operation, string inputExtension) => Find(operation, inputExtension) is not null;

    /// <summary>Operations that have an implementation at all, regardless of engine availability.</summary>
    public IEnumerable<string> ImplementedOperations => _converters.SelectMany(c => c.Operations).Distinct();
}
