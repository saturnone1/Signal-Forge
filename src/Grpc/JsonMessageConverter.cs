using Google.Protobuf;
using Google.Protobuf.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GrpcWorkbench.Grpc;

public interface IJsonMessageConverter
{
    Task<IMessage> JsonToMessageAsync(string json, Type messageType);
    string MessageToJson(IMessage message);
}

public class JsonMessageConverter : IJsonMessageConverter
{
    private readonly ILogger<JsonMessageConverter> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public JsonMessageConverter(ILogger<JsonMessageConverter> logger)
    {
        _logger = logger;
    }

    public Task<IMessage> JsonToMessageAsync(string json, Type messageType)
    {
        try
        {
            if (!typeof(IMessage).IsAssignableFrom(messageType))
                throw new ArgumentException($"Type {messageType.Name} does not implement IMessage");

            var parser = messageType.GetProperty("Parser")?.GetValue(null);
            if (parser == null)
                throw new InvalidOperationException($"No Parser property found on {messageType.Name}");

            var descriptor = messageType.GetProperty("Descriptor")?.GetValue(null) as MessageDescriptor;
            if (descriptor != null)
                json = NormalizeJsonForDescriptor(json, descriptor);

            var parseMethod = parser.GetType().GetMethod("ParseJson", [typeof(string)]);
            if (parseMethod == null)
                throw new InvalidOperationException($"No ParseJson method found on parser");

            var result = parseMethod.Invoke(parser, [json]) as IMessage
                ?? throw new InvalidOperationException("Failed to parse JSON");

            return Task.FromResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert JSON to message");
            throw;
        }
    }

    public string MessageToJson(IMessage message)
    {
        try
        {
            var formatter = new JsonFormatter(new JsonFormatter.Settings(true));
            return formatter.Format(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to convert message to JSON");
            throw;
        }
    }

    private static string NormalizeJsonForDescriptor(string json, MessageDescriptor descriptor)
    {
        using var doc = JsonDocument.Parse(json);
        var normalized = NormalizeElement(doc.RootElement, descriptor, null);
        if (normalized is Dictionary<string, object?> normalizedObject)
            EnsureHeaderMessageId(normalizedObject, descriptor);
        return JsonSerializer.Serialize(normalized, SerializerOptions);
    }

    private static void EnsureHeaderMessageId(Dictionary<string, object?> normalizedObject, MessageDescriptor descriptor)
    {
        var headerField = FindField(descriptor, "header");
        var headerDescriptor = headerField?.MessageType;
        if (headerField == null || headerDescriptor == null)
            return;

        var headerKey = headerField.JsonName;
        if (!normalizedObject.TryGetValue(headerKey, out var headerValue) || headerValue is null)
        {
            headerValue = new Dictionary<string, object?>();
            normalizedObject[headerKey] = headerValue;
        }

        if (headerValue is not Dictionary<string, object?> headerObject)
            return;

        EnsureHeaderEnumDefault(headerObject, headerDescriptor, "msgId", ResolveDefaultHeaderMessageId(descriptor, FindField(headerDescriptor, "msgId")?.EnumType));
        EnsureHeaderEnumDefault(headerObject, headerDescriptor, "srcSimId", ResolveDefaultEnumValue(FindField(headerDescriptor, "srcSimId")?.EnumType));
        EnsureHeaderEnumDefault(headerObject, headerDescriptor, "dstSimId", ResolveDefaultEnumValue(FindField(headerDescriptor, "dstSimId")?.EnumType));
    }

    private static void EnsureHeaderEnumDefault(Dictionary<string, object?> headerObject, MessageDescriptor headerDescriptor, string fieldName, string? defaultValue)
    {
        if (string.IsNullOrWhiteSpace(defaultValue))
            return;

        var field = FindField(headerDescriptor, fieldName);
        if (field == null)
            return;

        if (!headerObject.TryGetValue(field.JsonName, out var currentValue) || IsUnspecifiedEnumValue(currentValue))
            headerObject[field.JsonName] = defaultValue;
    }

    private static string? ResolveDefaultHeaderMessageId(MessageDescriptor descriptor, EnumDescriptor? enumDescriptor)
    {
        if (enumDescriptor == null)
            return null;

        foreach (var candidate in enumDescriptor.Values)
        {
            if (IdentifiersMatch(descriptor.Name, StripEnumPrefix(candidate.Name, enumDescriptor.Name)))
                return candidate.Name;
        }

        return null;
    }

    private static string? ResolveDefaultEnumValue(EnumDescriptor? enumDescriptor)
        => enumDescriptor?.Values.FirstOrDefault(candidate => candidate.Number != 0)?.Name;

    private static bool IsUnspecifiedEnumValue(object? value)
    {
        return value switch
        {
            null => true,
            sbyte signedByteValue => signedByteValue == 0,
            byte byteValue => byteValue == 0,
            short shortValue => shortValue == 0,
            ushort unsignedShortValue => unsignedShortValue == 0,
            int intValue => intValue == 0,
            uint unsignedIntValue => unsignedIntValue == 0,
            long longValue => longValue == 0,
            ulong unsignedLongValue => unsignedLongValue == 0,
            string stringValue => string.IsNullOrWhiteSpace(stringValue) || IdentifiersMatch(stringValue, "MESSAGE_ID_UNSPECIFIED") || IdentifiersMatch(stringValue, "UNSPECIFIED"),
            _ => false
        };
    }

    private static object? NormalizeElement(JsonElement element, MessageDescriptor? messageDescriptor, FieldDescriptor? fieldDescriptor)
    {
        if (fieldDescriptor != null)
        {
            if (fieldDescriptor.IsMap && element.ValueKind == JsonValueKind.Object)
                return NormalizeMapObject(element, fieldDescriptor);

            if (fieldDescriptor.IsRepeated && element.ValueKind == JsonValueKind.Array)
            {
                return element.EnumerateArray()
                    .Select(item => NormalizeRepeatedValue(item, fieldDescriptor))
                    .ToList();
            }

            if (fieldDescriptor.FieldType == FieldType.Message && fieldDescriptor.MessageType != null && element.ValueKind == JsonValueKind.Object)
                return NormalizeObject(element, fieldDescriptor.MessageType);

            if (fieldDescriptor.FieldType == FieldType.Enum && fieldDescriptor.EnumType != null)
                return NormalizeEnumValue(element, fieldDescriptor.EnumType);
        }

        if (messageDescriptor != null && element.ValueKind == JsonValueKind.Object)
            return NormalizeObject(element, messageDescriptor);

        return NormalizeScalar(element);
    }

    private static Dictionary<string, object?> NormalizeObject(JsonElement element, MessageDescriptor descriptor)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            var field = FindField(descriptor, property.Name);
            var key = field?.JsonName ?? property.Name;
            result[key] = NormalizeElement(property.Value, GetNestedMessageDescriptor(field), field);
        }

        return result;
    }

    private static Dictionary<string, object?> NormalizeMapObject(JsonElement element, FieldDescriptor descriptor)
    {
        var valueField = descriptor.MessageType?.FindFieldByNumber(2);
        var result = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
            result[property.Name] = NormalizeElement(property.Value, GetNestedMessageDescriptor(valueField), valueField);

        return result;
    }

    private static object? NormalizeRepeatedValue(JsonElement element, FieldDescriptor descriptor)
    {
        if (descriptor.FieldType == FieldType.Enum && descriptor.EnumType != null)
            return NormalizeEnumValue(element, descriptor.EnumType);

        if (descriptor.FieldType == FieldType.Message && descriptor.MessageType != null)
            return NormalizeElement(element, descriptor.MessageType, descriptor);

        return NormalizeScalar(element);
    }

    private static object? NormalizeEnumValue(JsonElement element, EnumDescriptor enumDescriptor)
    {
        if (element.ValueKind != JsonValueKind.String)
            return NormalizeScalar(element);

        var rawValue = element.GetString();
        if (string.IsNullOrWhiteSpace(rawValue))
            return rawValue;

        foreach (var candidate in enumDescriptor.Values)
        {
            if (IdentifiersMatch(rawValue, candidate.Name) || IdentifiersMatch(rawValue, StripEnumPrefix(candidate.Name, enumDescriptor.Name)))
                return candidate.Name;
        }

        return rawValue;
    }

    private static object? NormalizeScalar(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => NormalizeScalar(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeScalar).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var longValue)
                ? longValue
                : element.TryGetUInt64(out var ulongValue)
                    ? ulongValue
                    : element.TryGetDecimal(out var decimalValue)
                        ? decimalValue
                        : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };

    private static FieldDescriptor? FindField(MessageDescriptor descriptor, string propertyName)
    {
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            if (IdentifiersMatch(propertyName, field.Name) || IdentifiersMatch(propertyName, field.JsonName))
                return field;
        }

        return null;
    }

    private static MessageDescriptor? GetNestedMessageDescriptor(FieldDescriptor? field)
    {
        if (field == null)
            return null;

        return field.FieldType == FieldType.Message || field.IsMap
            ? field.MessageType
            : null;
    }

    private static bool IdentifiersMatch(string left, string right)
        => NormalizeIdentifier(left) == NormalizeIdentifier(right);

    private static string NormalizeIdentifier(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch))
                continue;

            buffer[index++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..index]);
    }

    private static string StripEnumPrefix(string valueName, string enumName)
    {
        var prefix = ToScreamingSnake(enumName) + "_";
        return valueName.StartsWith(prefix, StringComparison.Ordinal) ? valueName[prefix.Length..] : valueName;
    }

    private static string ToScreamingSnake(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length * 2);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (i > 0 && char.IsUpper(ch) && (char.IsLower(value[i - 1]) || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                builder.Append('_');

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }
}
