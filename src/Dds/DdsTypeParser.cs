using System.Xml.Linq;
using ASAP.Models.Dds;

namespace ASAP.Dds;

/// <summary>
/// RTI XML 타입 정의 (DDSSim.xml 형식) 파싱.
/// 결과는 UI 폼 생성용 메타데이터. 실제 DDS 타입 등록은 RTI QosProvider가 담당.
///
/// 지원 구조:
///   <types>
///     <module name="ENUM">
///       <enum name="ForceIdentifier">
///         <enumerator name="Other" value="0"/>
///         ...
///       </enum>
///     </module>
///     <module name="STRUCT">
///       <struct name="Position8">
///         <member name="X" type="float64"/>
///         <member name="Y" type="nonBasic" nonBasicTypeName="STRUCT::Other"/>
///         ...
///       </struct>
///     </module>
///   </types>
/// </summary>
public static class DdsTypeParser
{
    public static Dictionary<string, DdsTypeDefinition> Parse(string xmlContent)
    {
        var result = new Dictionary<string, DdsTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        var doc = XDocument.Parse(xmlContent);
        var typesElement = doc.Descendants().FirstOrDefault(element => element.Name.LocalName == "types");
        if (typesElement is null) return result;

        ParseContainer(result, typesElement, string.Empty);

        var definitions = result.Values.DistinctBy(item => item.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in definitions.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1) result[group.Key] = group.Single();
            else result.Remove(group.Key);
        }

        return result;
    }

    private static void ParseContainer(
        Dictionary<string, DdsTypeDefinition> result,
        XElement container,
        string modulePath)
    {
        foreach (var enumElement in Elements(container, "enum"))
            AddEnum(result, modulePath, enumElement);
        foreach (var structElement in Elements(container, "struct"))
            AddStruct(result, modulePath, structElement);
        foreach (var unionElement in Elements(container, "union"))
            AddUnion(result, modulePath, unionElement);
        foreach (var typedefElement in Elements(container, "typedef"))
            AddAlias(result, modulePath, typedefElement);

        foreach (var module in Elements(container, "module"))
        {
            var moduleName = module.Attribute("name")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(moduleName)) continue;
            var nestedPath = string.IsNullOrWhiteSpace(modulePath)
                ? moduleName
                : $"{modulePath}::{moduleName}";
            ParseContainer(result, module, nestedPath);
        }
    }

    private static void AddEnum(Dictionary<string, DdsTypeDefinition> map, string module, XElement enumEl)
    {
        var name = enumEl.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(name)) return;

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in Elements(enumEl, "enumerator"))
        {
            var enumName = v.Attribute("name")?.Value;
            var enumValueStr = v.Attribute("value")?.Value;
            if (string.IsNullOrEmpty(enumName)) continue;
            long val = 0;
            if (!string.IsNullOrEmpty(enumValueStr))
                long.TryParse(enumValueStr, out val);
            values[enumName] = val;
        }

        var qualified = string.IsNullOrEmpty(module) ? name : $"{module}::{name}";
        var def = new DdsTypeDefinition
        {
            Name = name,
            Kind = DdsTypeKind.Enum,
            EnumValues = values,
            QualifiedName = qualified,
        };
        map[qualified] = def;
        map[name] = def; // unqualified로도 접근 가능
    }

    private static void AddStruct(Dictionary<string, DdsTypeDefinition> map, string module, XElement structEl)
    {
        var name = structEl.Attribute("name")?.Value?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var members = new List<DdsTypeMember>();
        foreach (var m in Elements(structEl, "member"))
        {
            var memberName = m.Attribute("name")?.Value;
            var typeName = m.Attribute("type")?.Value;
            if (string.IsNullOrEmpty(memberName) || string.IsNullOrEmpty(typeName)) continue;

            string effectiveType = typeName == "nonBasic"
                ? m.Attribute("nonBasicTypeName")?.Value ?? typeName
                : typeName;

            var arrayDimsStr = m.Attribute("arrayDimensions")?.Value;
            int[]? arrayDims = null;
            if (!string.IsNullOrEmpty(arrayDimsStr))
            {
                arrayDims = arrayDimsStr
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => int.TryParse(s, out var n) ? n : 0)
                    .Where(n => n > 0)
                    .ToArray();
            }

            int? sequenceMax = null;
            if (int.TryParse(m.Attribute("sequenceMaxLength")?.Value, out var sm)) sequenceMax = sm;
            int? stringMax = null;
            if (int.TryParse(m.Attribute("stringMaxLength")?.Value, out var smx)) stringMax = smx;

            members.Add(new DdsTypeMember
            {
                Name = memberName,
                TypeName = effectiveType,
                IsSequence = sequenceMax.HasValue,
                IsArray = arrayDims is { Length: > 0 },
                ArrayDimensions = arrayDims,
                SequenceMaxLength = sequenceMax,
                StringMaxLength = stringMax,
            });
        }

        var qualified = string.IsNullOrEmpty(module) ? name : $"{module}::{name}";
        var def = new DdsTypeDefinition
        {
            Name = name,
            Kind = DdsTypeKind.Struct,
            Members = members,
            QualifiedName = qualified,
        };
        map[qualified] = def;
        map[name] = def;
    }

    private static void AddUnion(Dictionary<string, DdsTypeDefinition> map, string module, XElement unionElement)
    {
        var name = unionElement.Attribute("name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        var discriminator = Elements(unionElement, "discriminator").FirstOrDefault();
        var unionCases = Elements(unionElement, "case").Select(unionCase =>
        {
            var member = Elements(unionCase, "member").FirstOrDefault();
            var parsed = member == null ? null : ParseMember(member);
            return parsed == null ? null : new DdsUnionCaseDefinition
            {
                Labels = Elements(unionCase, "caseDiscriminator")
                    .Select(label => label.Attribute("value")?.Value?.Trim() ?? string.Empty)
                    .Where(label => label.Length > 0).ToList(),
                Member = parsed,
            };
        }).Where(item => item != null).Cast<DdsUnionCaseDefinition>().ToList();
        var qualified = string.IsNullOrEmpty(module) ? name : $"{module}::{name}";
        var definition = new DdsTypeDefinition
        {
            Name = name,
            Kind = DdsTypeKind.Union,
            Members = unionCases.Select(item => item.Member).ToList(),
            UnionDiscriminatorTypeName = discriminator == null ? null : EffectiveType(discriminator),
            UnionCases = unionCases,
            QualifiedName = qualified,
        };
        map[qualified] = definition;
        map[name] = definition;
    }

    private static void AddAlias(Dictionary<string, DdsTypeDefinition> map, string module, XElement typedefElement)
    {
        var name = typedefElement.Attribute("name")?.Value?.Trim();
        var type = typedefElement.Attribute("type")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type)) return;

        var target = type == "nonBasic"
            ? typedefElement.Attribute("nonBasicTypeName")?.Value?.Trim() ?? type
            : type;
        var qualified = string.IsNullOrEmpty(module) ? name : $"{module}::{name}";
        var definition = new DdsTypeDefinition
        {
            Name = name,
            Kind = DdsTypeKind.Alias,
            QualifiedName = qualified,
            AliasTargetName = target,
            AliasIsSequence = typedefElement.Attribute("sequenceMaxLength") != null,
            AliasSequenceMaxLength = ParseNullableInt(typedefElement.Attribute("sequenceMaxLength")?.Value),
            AliasArrayDimensions = ParseDimensions(typedefElement.Attribute("arrayDimensions")?.Value),
            AliasStringMaxLength = ParseNullableInt(typedefElement.Attribute("stringMaxLength")?.Value),
        };
        map[qualified] = definition;
        map[name] = definition;
    }

    private static DdsTypeMember? ParseMember(XElement memberElement)
    {
        var memberName = memberElement.Attribute("name")?.Value;
        var typeName = memberElement.Attribute("type")?.Value;
        if (string.IsNullOrEmpty(memberName) || string.IsNullOrEmpty(typeName)) return null;

        var effectiveType = typeName == "nonBasic"
            ? memberElement.Attribute("nonBasicTypeName")?.Value ?? typeName
            : typeName;
        var arrayDimensions = memberElement.Attribute("arrayDimensions")?.Value?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 0)
            .Where(value => value > 0)
            .ToArray();
        int? sequenceMax = int.TryParse(memberElement.Attribute("sequenceMaxLength")?.Value, out var sequence)
            ? sequence
            : null;
        int? stringMax = int.TryParse(memberElement.Attribute("stringMaxLength")?.Value, out var stringLength)
            ? stringLength
            : null;

        return new DdsTypeMember
        {
            Name = memberName,
            TypeName = effectiveType,
            IsSequence = memberElement.Attribute("sequenceMaxLength") != null,
            IsArray = arrayDimensions is { Length: > 0 },
            ArrayDimensions = arrayDimensions,
            SequenceMaxLength = sequenceMax,
            StringMaxLength = stringMax,
        };
    }

    private static IEnumerable<XElement> Elements(XElement parent, string localName)
        => parent.Elements().Where(element => element.Name.LocalName == localName);

    private static string EffectiveType(XElement element)
        => element.Attribute("type")?.Value == "nonBasic"
            ? element.Attribute("nonBasicTypeName")?.Value ?? "nonBasic"
            : element.Attribute("type")?.Value ?? string.Empty;

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static int[]? ParseDimensions(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => int.TryParse(item, out var parsed) ? parsed : 0).Where(item => item > 0).ToArray();
}
