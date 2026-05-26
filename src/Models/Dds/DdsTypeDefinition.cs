namespace GrpcWorkbench.Models.Dds;

public enum DdsTypeKind { Struct, Enum, Primitive }

public sealed class DdsTypeDefinition
{
    public required string Name { get; init; }
    public required DdsTypeKind Kind { get; init; }

    // Struct 멤버 (Kind=Struct)
    public List<DdsTypeMember> Members { get; init; } = [];

    // Enum 값 (Kind=Enum)
    public Dictionary<string, long> EnumValues { get; init; } = [];

    // Module prefix가 포함된 정규화된 이름 (예: MSG::AirThreatInformation, ENUM::ForceIdentifier)
    public string? QualifiedName { get; init; }
}

public sealed class DdsTypeMember
{
    public required string Name { get; init; }
    public required string TypeName { get; init; }
    public bool IsSequence { get; init; }
    public bool IsArray { get; init; }
    public int[]? ArrayDimensions { get; init; }
    public int? MaxLength { get; init; }
    public int? SequenceMaxLength { get; init; }
    public int? StringMaxLength { get; init; }
}
