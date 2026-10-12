using ASAP.Models.Dds;

namespace ASAP.Dds;

// Module identity is part of every lookup: equal wire names never merge independent XML schemas.
internal sealed class DdsSchemaCatalog
{
    private readonly IReadOnlyDictionary<(string Module, string WireType), DdsTypeDefinition> _types;
    private readonly IReadOnlyDictionary<(string Module, string WireType), IReadOnlyDictionary<string, DdsTypeMember>> _members;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, (string Module, string WireType)> _keys = new();

    public DdsSchemaCatalog(IEnumerable<(string Name, string Xml)> contracts)
    {
        _types = contracts.SelectMany(contract => DdsTypeParser.Parse(contract.Xml)
            .Where(pair => pair.Key == pair.Value.QualifiedName)
            .Select(pair => new KeyValuePair<(string, string), DdsTypeDefinition>((contract.Name, pair.Key), pair.Value)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        _members = _types.ToDictionary(pair => pair.Key,
            pair => (IReadOnlyDictionary<string, DdsTypeMember>)pair.Value.Members.ToDictionary(member => member.Name, StringComparer.Ordinal));
    }

    public DdsTypeDefinition Get(Type type)
    {
        var key = _keys.GetOrAdd(type, static candidate => ResolveKey(candidate));
        return _types.TryGetValue(key, out var definition) ? definition
            : throw new InvalidOperationException($"No built schema for module '{key.Module}', wire type '{key.WireType}'.");
    }

    public DdsTypeMember? GetMember(Type type, string name)
    {
        var key = _keys.GetOrAdd(type, static candidate => ResolveKey(candidate));
        if (!_members.TryGetValue(key, out var members)) { _ = Get(type); return null; }
        return members.TryGetValue(name, out var member) ? member : null;
    }

    private static (string Module, string WireType) ResolveKey(Type type)
    {
        var prefix = type.FullName?.Split('.')[0] ?? string.Empty;
        if (!prefix.StartsWith("DdsModules_", StringComparison.Ordinal))
            throw new InvalidOperationException($"'{type}' is not a generated module type.");
        return (prefix["DdsModules_".Length..], BuiltMessageRegistry.WireTypeName(type));
    }
}
