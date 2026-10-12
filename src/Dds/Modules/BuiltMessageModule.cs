using System.Collections.ObjectModel;
using System.Xml.Linq;
using DdsAmbassador.DDSClient;

namespace ASAP.Dds;

public sealed class BuiltMessageModule
{
    internal BuiltMessageModule(DdsMessageModule module, string contractXml)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(contractXml.Replace("\r\n", "\n"))));
        if (!string.Equals(module.SchemaSha256, hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Built module '{module.Name}' schema hash does not match its XML. Regenerate and rebuild the module.");
        Module = module;
        ContractXml = contractXml;
        CanonicalContract = BuiltMessageRegistry.Canonicalize(XDocument.Parse(contractXml));
        Types = new ReadOnlyDictionary<string, Type>(module.Types.Values.ToDictionary(BuiltMessageRegistry.WireTypeName, StringComparer.Ordinal));
    }
    public DdsMessageModule Module { get; }
    public string ContractXml { get; }
    internal string CanonicalContract { get; }
    public IReadOnlyDictionary<string, Type> Types { get; }
    public Type ResolveType(string wireName) => Types.TryGetValue(wireName, out var type) ? type
        : throw new InvalidOperationException($"Unsupported XML type '{wireName}' for built module '{Module.Name}'.");
    public DdsTypedSample CreateSample(string wireName) => new(Activator.CreateInstance(ResolveType(wireName))!);
}
