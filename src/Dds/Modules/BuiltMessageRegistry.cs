using System.Collections.ObjectModel;
using System.Xml.Linq;
using DdsAmbassador.DDSClient;
using DdsAmbassador.Messages;

namespace ASAP.Dds;

/// <summary>실행 시 발견한 빌드 모듈 목록이다. 세션별로 사용할 모듈을 선택하며 XML로 타입을 생성하지 않는다.</summary>
public static class BuiltMessageRegistry
{
    public static IReadOnlyList<BuiltMessageModule> Modules { get; } = Array.AsReadOnly(MessageModuleLoader.Load());

    public static BuiltMessageModule Get(string name) => Modules.SingleOrDefault(module => module.Module.Name == name)
        ?? throw new InvalidOperationException($"Unsupported built message module '{name}'. Available: {string.Join(", ", Modules.Select(m => m.Module.Name))}.");

    public static BuiltMessageModule Select(string xml, string? moduleName = null)
    {
        var canonical = Canonicalize(XDocument.Parse(xml));
        var candidates = moduleName is null ? Modules : new[] { Get(moduleName) };
        var matches = candidates.Where(module => module.CanonicalContract == canonical).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("여러 모듈이 같은 XML 계약을 사용합니다. 사용할 메시지 모듈을 명시적으로 선택하세요.");
        return matches.SingleOrDefault()
            ?? throw new InvalidOperationException("메시지 XML이 선택한 빌드 모듈과 다릅니다. 배포된 계약을 가져오거나 모듈을 다시 빌드하세요.");
    }

    public static void ValidateContract(string xml) => _ = Select(xml);

    public static IReadOnlyDictionary<string, BuiltTopic> ValidateAndBind(string xml, string topicSettingsJson, string qosXml,
        string? moduleName = null, string? qosLibraryName = null)
    {
        var module = Select(xml, moduleName);
        var config = DdsConfigParser.Parse(topicSettingsJson, qosXml, module, qosLibraryName);
        return new ReadOnlyDictionary<string, BuiltTopic>(config.Topics.ToDictionary(topic => topic.TopicName, topic =>
        {
            var type = module.ResolveType(topic.TypeName);
            var definition = new TopicDefinition(topic.TopicName, config.QosLibraryName + "::" + topic.QosProfileName,
                Enum.Parse<TopicDirection>(topic.Direction.ToString())) { SampleType = type };
            return (BuiltTopic)Activator.CreateInstance(typeof(BuiltTopic<>).MakeGenericType(type), definition, topic.TypeName)!;
        }, StringComparer.Ordinal));
    }

    public static string DefaultWireTypeName(string name)
    {
        var matches = Modules.SelectMany(m => m.Module.Types).Where(p => p.Key == name).Select(p => WireTypeName(p.Value)).Distinct().ToArray();
        return matches.Length == 1 ? matches[0] : $"MSG::{name}"; // legacy editor drafts; validated against the selected module before use
    }

    public static string WireTypeName(Type type)
    {
        var name = type.FullName ?? throw new InvalidOperationException("Generated message has no CLR name.");
        var parts = name.Split('.');
        if (parts.Length < 2 || !parts[0].StartsWith("DdsModules_", StringComparison.Ordinal))
            throw new InvalidOperationException($"Generated type '{name}' lacks the expected module namespace prefix.");
        return string.Join("::", parts.Skip(1));
    }

    internal static string Canonicalize(XDocument document)
    {
        static XElement Normalize(XElement element) => new(element.Name,
            element.Attributes().OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)),
            element.Nodes().Where(n => n is XElement || n is XText text && !string.IsNullOrWhiteSpace(text.Value))
                .Select(n => n is XElement child ? (object)Normalize(child) : ((XText)n).Value.Trim()));
        if (document.Root is null) throw new InvalidOperationException("Missing DDS XML root.");
        if (document.DescendantNodes().OfType<XProcessingInstruction>().Any())
            throw new InvalidOperationException("DDS type directives are not supported by the built-module adapter.");
        return Normalize(document.Root).ToString(SaveOptions.DisableFormatting);
    }
}
