using System.Xml.Linq;
using GrpcWorkbench.Models.Dds;

namespace GrpcWorkbench.Dds;

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
        var typesElement = doc.Descendants("types").FirstOrDefault();
        if (typesElement is null) return result;

        foreach (var module in typesElement.Elements("module"))
        {
            var moduleName = module.Attribute("name")?.Value ?? string.Empty;

            foreach (var enumEl in module.Elements("enum"))
                AddEnum(result, moduleName, enumEl);

            foreach (var structEl in module.Elements("struct"))
                AddStruct(result, moduleName, structEl);
        }

        // 모듈 밖에 있는 enum/struct도 흡수 (간단한 XML 호환)
        foreach (var enumEl in typesElement.Elements("enum"))
            AddEnum(result, string.Empty, enumEl);
        foreach (var structEl in typesElement.Elements("struct"))
            AddStruct(result, string.Empty, structEl);

        return result;
    }

    private static void AddEnum(Dictionary<string, DdsTypeDefinition> map, string module, XElement enumEl)
    {
        var name = enumEl.Attribute("name")?.Value;
        if (string.IsNullOrEmpty(name)) return;

        var values = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in enumEl.Elements("enumerator"))
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
        foreach (var m in structEl.Elements("member"))
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
}
