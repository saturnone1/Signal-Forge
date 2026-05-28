namespace ASAP.Models.Ui;

public class SchemaField
{
    public string Name { get; set; } = "";
    /// <summary>string | integer | number | boolean | enum | object | array</summary>
    public string Type { get; set; } = "string";
    public string[] EnumValues { get; set; } = [];
    /// <summary>스칼라/fallback 값 (JSON 텍스트)</summary>
    public string Value { get; set; } = "";
    public bool BoolValue { get; set; } = false;
    /// <summary>object 타입일 때 중첩 필드</summary>
    public List<SchemaField> Children { get; set; } = [];
    /// <summary>array 타입의 아이템 템플릿 (items 스키마)</summary>
    public SchemaField? ItemTemplate { get; set; }
    /// <summary>array 타입의 실제 항목 목록</summary>
    public List<SchemaField> ArrayItems { get; set; } = [];

    public SchemaField DeepClone(string? overrideName = null) => new()
    {
        Name = overrideName ?? Name,
        Type = Type,
        EnumValues = EnumValues,
        Value = Type switch
        {
            "enum"   => EnumValues.FirstOrDefault() ?? "",
            "object" => "{}",
            "array"  => "[]",
            "integer" or "number" => "0",
            _ => ""
        },
        BoolValue = false,
        Children = Children.Select(c => c.DeepClone()).ToList(),
        ItemTemplate = ItemTemplate?.DeepClone()
    };
}
