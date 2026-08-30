using Rti.Dds.Topics;
using Rti.Types.Dynamic;

namespace ASAP.Dds;

/// <summary>
/// DynamicData ↔ JSON 변환. RTI Connext의 내장 PrintFormat/FromString을 활용.
/// - DynamicData → JSON: ToString(PrintFormatProperty { Kind = Json })
/// - JSON → DynamicData: FromString(json, PrintFormatKind.Json)
/// 직접 멤버를 순회하지 않으므로 모든 RTI 지원 타입(struct/enum/sequence/array/union/alias)을 자동 처리.
/// </summary>
public static class DdsJsonConverter
{
    private static readonly PrintFormatProperty _jsonCompact = new()
    {
        Kind = PrintFormatKind.Json,
        PrettyPrint = false,
        EnumAsInt = false,
        IncludeRootElements = false,
    };

    private static readonly PrintFormatProperty _jsonPretty = new()
    {
        Kind = PrintFormatKind.Json,
        PrettyPrint = true,
        EnumAsInt = false,
        IncludeRootElements = false,
    };

    public static string ToJson(DynamicData data, bool pretty = false)
    {
        var json = data.ToString(pretty ? _jsonPretty : _jsonCompact);
        return NormalizeJson(json);
    }

    public static string NormalizeJson(string json)
    {
        var trimmed = json.Trim();

        // Connext의 IncludeRootElements=false 출력은 최상위 struct에서
        // `"member": ...` 목록만 반환할 수 있다. UI와 재발행 계약을 위해
        // 항상 유효한 JSON object로 정규화한다.
        return LooksLikeRootMemberList(trimmed) ? $"{{{trimmed}}}" : json;
    }

    public static void ApplyJson(DynamicData target, string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        target.FromString(json, PrintFormatKind.Json);
    }

    private static bool LooksLikeRootMemberList(string value)
        => value.Length > 2 && value[0] == '"' && value.Contains("\":", StringComparison.Ordinal);
}
