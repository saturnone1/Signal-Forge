namespace ASAP.Models.Dds;

public enum DdsTypeKind { Struct, Enum, Union, Alias, Primitive }

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

    // Typedef/alias의 대상 타입. primitive 또는 scoped non-basic 이름일 수 있다.
    public string? AliasTargetName { get; init; }
    public bool AliasIsSequence { get; init; }
    public int? AliasSequenceMaxLength { get; init; }
    public int[]? AliasArrayDimensions { get; init; }
    public int? AliasStringMaxLength { get; init; }

    public string? UnionDiscriminatorTypeName { get; init; }
    public List<DdsUnionCaseDefinition> UnionCases { get; init; } = [];
}

public sealed class DdsUnionCaseDefinition
{
    public required IReadOnlyList<string> Labels { get; init; }
    public required DdsTypeMember Member { get; init; }
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
