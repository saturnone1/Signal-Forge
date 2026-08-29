namespace ASAP.Models.Dds;

public sealed class DdsXmlProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "DDS 프로필";
    public string TypesXml { get; set; } = string.Empty;
    public string ConfigXml { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DdsXmlProfile Copy(string? name = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Name = name ?? Name,
        TypesXml = TypesXml,
        ConfigXml = ConfigXml,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
    };
}

public sealed class DdsProfileCatalog
{
    public int Version { get; set; } = 1;
    public long Revision { get; set; }
    public List<DdsXmlProfile> Profiles { get; set; } = [];
}

public sealed record DdsProfileValidationResult(int TypeCount, int TopicCount, int QosProfileCount);
