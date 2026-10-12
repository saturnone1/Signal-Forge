using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Reflection;

namespace ASAP.Dds;

/// <summary>JSON bridge for the explicitly registered generated CLR messages, including get-only sequences.</summary>
public static class DdsJsonConverter
{
    private static readonly JsonSerializerOptions Compact = CreateOptions(false);
    private static readonly JsonSerializerOptions Pretty = CreateOptions(true);
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ReadableProperties = new();
    private sealed record JsonShape(IReadOnlyDictionary<string, PropertyInfo> Properties, Type? ItemType);
    private static readonly ConcurrentDictionary<Type, JsonShape> JsonShapes = new();
    private static JsonShape Shape(Type type) => JsonShapes.GetOrAdd(type, static candidate => new(
        candidate.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, StringComparer.Ordinal),
        candidate.IsArray ? candidate.GetElementType() : candidate.GetInterfaces().Append(candidate)
            .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0]));
    // 검증은 기본값을 읽기만 한다. 메시지·시퀀스 원소마다 기본 객체를 다시 생성하지 않는다.
    private static readonly ConcurrentDictionary<Type, Lazy<object?>> DefaultTemplates = new();
    private static object? DefaultTemplate(Type type) => DefaultTemplates.GetOrAdd(type,
        static candidate => new Lazy<object?>(() => Activator.CreateInstance(candidate), LazyThreadSafetyMode.PublicationOnly)).Value;
    private static readonly Lazy<DdsSchemaCatalog> Schemas = new(() => new(
        BuiltMessageRegistry.Modules.Select(m => (m.Module.Name, m.ContractXml))));

    private static JsonSerializerOptions CreateOptions(bool pretty)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = pretty,
            PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string ToJson(DdsTypedSample sample, bool pretty = false) => ToJson(sample.Value, pretty);
    public static string ToJson(object sample, bool pretty = false) =>
        JsonSerializer.Serialize(sample, sample.GetType(), pretty ? Pretty : Compact);

    public static void ApplyJson(DdsTypedSample target, string json)
        => ApplyJson(target, json, Schemas.Value);

    internal static void ApplyJson(DdsTypedSample target, string json, DdsSchemaCatalog schemas)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("DDS payload must be a JSON object, not null or an array.");
        ValidateJsonNulls(document.RootElement, target.Value.GetType(), "$", schemas);
        // 이미 파싱·검증한 UTF-8 문서를 재사용하여 같은 문자열을 다시 인코딩하지 않는다.
        var value = JsonSerializer.Deserialize(document.RootElement, target.Value.GetType(), Compact)
            ?? throw new JsonException("DDS payload must be a JSON object, not null.");
        ValidateValue(value, DefaultTemplate(value.GetType()), "$", 0, schemas);
        target.Value = value;
    }

    // get-only 컬렉션은 역직렬화기가 null을 무시할 수도 있다. 원문을 먼저 검사하여 필수 멤버의 null을 차단한다.
    private static void ValidateJsonNulls(JsonElement element, Type type, string path, DdsSchemaCatalog schemas, bool optional = false)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!optional) throw new JsonException($"DDS 멤버 '{path}'은 null일 수 없습니다.");
            return;
        }
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = Shape(type).Properties;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in element.EnumerateObject())
            {
                if (!names.Add(field.Name)) throw new JsonException($"중복 DDS 멤버입니다: {path}.{field.Name}");
                if (!properties.TryGetValue(field.Name, out var property)) continue; // 알 수 없는 필드는 역직렬화기의 Disallow 규칙으로 거부한다.
                ValidateJsonNulls(field.Value, property.PropertyType, path + "." + field.Name, schemas,
                    schemas.GetMember(type, field.Name)?.Optional == true);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var itemType = Shape(type).ItemType;
            if (itemType is not null)
                foreach (var item in element.EnumerateArray()) ValidateJsonNulls(item, itemType, path + "[]", schemas);
        }
    }

    private static void ValidateValue(object? value, object? template, string path, int depth, DdsSchemaCatalog schemas)
    {
        if (value is null) throw new JsonException($"DDS member '{path}' cannot be null.");
        if (depth > 64) throw new JsonException("DDS payload exceeds the supported nesting depth.");
        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is string || value is decimal) return;
        if (value is System.Collections.IEnumerable collection)
        {
            if (value is Array array && template is Array expected && array.Length != expected.Length)
                throw new JsonException($"DDS fixed array '{path}' requires {expected.Length} elements; received {array.Length}.");
            foreach (var item in collection)
                ValidateValue(item, item is null || item is string ? null : DefaultTemplate(item.GetType()), path + "[]", depth + 1, schemas);
            return;
        }
        _ = schemas.Get(type);
        foreach (var property in ReadableProperties.GetOrAdd(type,
            static t => t.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0).ToArray()))
        {
            var member = schemas.GetMember(type, property.Name);
            var field = property.GetValue(value);
            if (field is null && member?.Optional == true) continue;
            if (member?.StringMaxLength is int maxString && field is string text && System.Text.Encoding.UTF8.GetByteCount(text) > maxString)
                throw new JsonException($"DDS string '{path}.{property.Name}' exceeds {maxString} UTF-8 bytes.");
            if (member?.SequenceMaxLength is int maxSequence && field is System.Collections.IEnumerable sequence && ExceedsSequenceBound(sequence, maxSequence))
                throw new JsonException($"DDS sequence '{path}.{property.Name}' exceeds {maxSequence} elements.");
            ValidateValue(field, template is null ? null : property.GetValue(template), path + "." + property.Name, depth + 1, schemas);
        }
    }

    private static bool ExceedsSequenceBound(System.Collections.IEnumerable sequence, int maximum)
    {
        // 배열·List는 상수 시간 Count를 사용한다. 그 외 컬렉션도 한도를 넘으면 즉시 멈춘다.
        if (sequence is System.Collections.ICollection collection) return collection.Count > maximum;
        var count = 0;
        foreach (var unused in sequence)
        {
            if (count >= maximum) return true;
            count++;
        }
        return false;
    }

    public static string NormalizeJson(string json)
    {
        var trimmed = json.Trim();
        return trimmed.Length > 2 && trimmed[0] == '"' && trimmed.Contains("\":", StringComparison.Ordinal)
            ? $"{{{trimmed}}}" : json;
    }
}
