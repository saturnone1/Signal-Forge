using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ASAP.Services;

public sealed record DdsTypeRename(string OldName, string NewName);

public sealed class DdsTypeEditorState
{
    public List<string> ModulePaths { get; set; } = [string.Empty];
    public List<DdsTypeDeclarationEditor> Declarations { get; set; } = [];
    public int PreservedUnsupportedElementCount { get; set; }
}

public sealed class DdsTypeDeclarationEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = DdsTypeProfileEditor.StructKind;
    public string ModulePath { get; set; } = string.Empty;
    public string Name { get; set; } = "NewType";
    public string? SourceXml { get; set; }

    public string Extensibility { get; set; } = string.Empty;
    public string AutoId { get; set; } = string.Empty;
    public string BaseType { get; set; } = string.Empty;
    public bool Nested { get; set; }
    public List<DdsTypeMemberEditor> Members { get; set; } = [];

    public List<DdsEnumValueEditor> EnumValues { get; set; } = [];

    public string DiscriminatorTypeName { get; set; } = "int32";
    public List<DdsUnionCaseEditor> UnionCases { get; set; } = [];

    public string TypeName { get; set; } = "int32";
    public string StringMaxLength { get; set; } = string.Empty;
    public string SequenceMaxLength { get; set; } = string.Empty;
    public string ArrayDimensions { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public string MinValue { get; set; } = string.Empty;
    public string MaxValue { get; set; } = string.Empty;
}

public sealed class DdsTypeMemberEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "member";
    public string TypeName { get; set; } = "int32";
    public string StringMaxLength { get; set; } = string.Empty;
    public string SequenceMaxLength { get; set; } = string.Empty;
    public string ArrayDimensions { get; set; } = string.Empty;
    public bool Key { get; set; }
    public bool Optional { get; set; }
    public bool External { get; set; }
    public string MemberId { get; set; } = string.Empty;
    public string DefaultValue { get; set; } = string.Empty;
    public string MinValue { get; set; } = string.Empty;
    public string MaxValue { get; set; } = string.Empty;
    public string? SourceXml { get; set; }
}

public sealed class DdsEnumValueEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "VALUE";
    public string Value { get; set; } = string.Empty;
    public bool DefaultLiteral { get; set; }
    public string? SourceXml { get; set; }
}

public sealed class DdsUnionCaseEditor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Labels { get; set; } = "0";
    public DdsTypeMemberEditor Member { get; set; } = new();
    public string? SourceXml { get; set; }
}

public static partial class DdsTypeProfileEditor
{
    public const string StructKind = "struct";
    public const string EnumKind = "enum";
    public const string UnionKind = "union";
    public const string TypedefKind = "typedef";
    public const string ConstKind = "const";

    public static readonly string[] PrimitiveTypes =
    [
        "boolean", "byte", "int8", "uint8", "char8", "char16",
        "int16", "uint16", "int32", "uint32", "int64", "uint64",
        "float32", "float64", "float128", "string", "wstring",
    ];

    public static readonly string[] DeclarationKinds =
        [StructKind, EnumKind, UnionKind, TypedefKind, ConstKind];

    private static readonly HashSet<string> SupportedDeclarationNames =
        new(DeclarationKinds, StringComparer.Ordinal);

    private static readonly HashSet<string> PrimitiveTypeNames =
        new(PrimitiveTypes.Concat(
        [
            "octet", "long", "float", "double", "char", "wchar", "char32",
            "short", "unsignedShort", "unsignedLong", "longLong",
            "unsignedLongLong", "longDouble",
        ]), StringComparer.Ordinal);

    public static DdsTypeEditorState Parse(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var types = FindTypes(document)
                    ?? throw new InvalidOperationException("타입 XML에서 <types> 요소를 찾지 못했습니다.");
        var state = new DdsTypeEditorState();
        ParseContainer(types, string.Empty, state);
        state.ModulePaths = state.ModulePaths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        return state;
    }

    public static string Apply(string originalXml, DdsTypeEditorState state)
    {
        ValidateState(state);
        var document = XDocument.Parse(originalXml, LoadOptions.PreserveWhitespace);
        var types = FindTypes(document)
                    ?? throw new InvalidOperationException("타입 XML에서 <types> 요소를 찾지 못했습니다.");

        foreach (var container in types.DescendantsAndSelf()
                     .Where(element => element == types || element.Name.LocalName == "module")
                     .ToList())
        {
            container.Elements()
                .Where(element => SupportedDeclarationNames.Contains(element.Name.LocalName))
                .Remove();
        }

        foreach (var modulePath in state.ModulePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            EnsureModule(types, modulePath);

        foreach (var declaration in state.Declarations)
        {
            var container = EnsureModule(types, declaration.ModulePath);
            container.Add(BuildDeclaration(declaration));
        }

        return document.ToString();
    }

    public static void ValidateState(DdsTypeEditorState state)
    {
        if (state.Declarations.Count == 0)
            throw new InvalidOperationException("DDS 타입을 하나 이상 정의하세요.");

        foreach (var modulePath in state.ModulePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            ValidateScopedName(modulePath, "모듈 경로");

        var duplicate = state.Declarations
            .GroupBy(QualifiedName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"중복 DDS 타입 이름: {duplicate.Key}");
        var declarationsByName = state.Declarations.ToDictionary(QualifiedName, StringComparer.OrdinalIgnoreCase);

        foreach (var declaration in state.Declarations)
        {
            ValidateIdentifier(declaration.Name, "타입 이름");
            if (!string.IsNullOrWhiteSpace(declaration.ModulePath))
                ValidateScopedName(declaration.ModulePath, "모듈 경로");

            switch (declaration.Kind)
            {
                case StructKind:
                    if (declaration.Members.Count == 0)
                        throw new InvalidOperationException($"struct '{QualifiedName(declaration)}'에 member를 하나 이상 추가하세요.");
                    ValidateMembers(declaration.Members, QualifiedName(declaration), allowKeyAndOptional: true);
                    if (!string.IsNullOrWhiteSpace(declaration.BaseType))
                        ValidateScopedName(declaration.BaseType, "baseType");
                    break;
                case EnumKind:
                    if (declaration.EnumValues.Count == 0)
                        throw new InvalidOperationException($"enum '{QualifiedName(declaration)}'에 값을 하나 이상 추가하세요.");
                    ValidateEnumValues(declaration);
                    break;
                case UnionKind:
                    ValidateTypeName(declaration.DiscriminatorTypeName, "union discriminator");
                    if (declaration.UnionCases.Count == 0)
                        throw new InvalidOperationException($"union '{QualifiedName(declaration)}'에 case를 하나 이상 추가하세요.");
                    var labelsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var defaultCount = 0;
                    foreach (var unionCase in declaration.UnionCases)
                    {
                        var labels = SplitList(unionCase.Labels);
                        if (labels.Count == 0)
                            throw new InvalidOperationException($"union '{QualifiedName(declaration)}' case의 판별값을 입력하세요.");
                        ValidateMembers([unionCase.Member], QualifiedName(declaration), allowKeyAndOptional: false);
                        foreach (var label in labels)
                        {
                            if (!labelsSeen.Add(label))
                                throw new InvalidOperationException($"union '{QualifiedName(declaration)}'에 중복 case 값이 있습니다: {label}");
                            if (label.Equals("default", StringComparison.OrdinalIgnoreCase)) defaultCount++;
                            else if (IsIntegerDiscriminator(declaration.DiscriminatorTypeName) && !long.TryParse(label, out _))
                                throw new InvalidOperationException($"union '{QualifiedName(declaration)}'의 case 값 '{label}'은 정수여야 합니다.");
                        }
                    }
                    if (defaultCount > 1)
                        throw new InvalidOperationException($"union '{QualifiedName(declaration)}'에는 default case를 하나만 둘 수 있습니다.");
                    break;
                case TypedefKind:
                    ValidateTypeName(declaration.TypeName, "typedef 타입");
                    ValidateBounds(declaration.StringMaxLength, declaration.SequenceMaxLength, declaration.ArrayDimensions, declaration.Name);
                    break;
                case ConstKind:
                    ValidateTypeName(declaration.TypeName, "const 타입");
                    if (string.IsNullOrWhiteSpace(declaration.Value))
                        throw new InvalidOperationException($"const '{QualifiedName(declaration)}'의 값을 입력하세요.");
                    break;
                default:
                    throw new InvalidOperationException($"지원하지 않는 DDS 선언 종류: {declaration.Kind}");
            }

            foreach (var reference in ReferencedTypes(declaration))
            {
                if (PrimitiveTypeNames.Contains(reference)) continue;
                var relative = string.IsNullOrWhiteSpace(declaration.ModulePath) ? reference : $"{declaration.ModulePath}::{reference}";
                if (!declarationsByName.ContainsKey(reference) && !declarationsByName.ContainsKey(relative))
                    throw new InvalidOperationException($"'{QualifiedName(declaration)}'이(가) 존재하지 않는 타입 '{reference}'을 참조합니다.");
            }
        }
        ValidateAliasCycles(state.Declarations);
    }

    public static string QualifiedName(DdsTypeDeclarationEditor declaration)
        => string.IsNullOrWhiteSpace(declaration.ModulePath)
            ? declaration.Name.Trim()
            : $"{declaration.ModulePath.Trim()}::{declaration.Name.Trim()}";

    public static bool IsValidIdentifier(string? value)
        => !string.IsNullOrWhiteSpace(value) && IdentifierRegex().IsMatch(value.Trim());

    private static void ParseContainer(XElement container, string modulePath, DdsTypeEditorState state)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name.LocalName == "module")
            {
                var name = Attribute(child, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                var nestedPath = string.IsNullOrWhiteSpace(modulePath) ? name : $"{modulePath}::{name}";
                state.ModulePaths.Add(nestedPath);
                ParseContainer(child, nestedPath, state);
                continue;
            }

            if (!SupportedDeclarationNames.Contains(child.Name.LocalName))
            {
                if (child.Name.LocalName is not "include" and not "forward_dcl")
                    state.PreservedUnsupportedElementCount++;
                continue;
            }

            state.Declarations.Add(ParseDeclaration(child, modulePath));
        }
    }

    private static DdsTypeDeclarationEditor ParseDeclaration(XElement element, string modulePath)
    {
        var declaration = new DdsTypeDeclarationEditor
        {
            Kind = element.Name.LocalName,
            ModulePath = modulePath,
            Name = Attribute(element, "name"),
            SourceXml = element.ToString(SaveOptions.DisableFormatting),
            Extensibility = Attribute(element, "extensibility"),
            AutoId = Attribute(element, "autoid"),
            BaseType = Attribute(element, "baseType"),
            Nested = BooleanAttribute(element, "nested"),
        };

        switch (declaration.Kind)
        {
            case StructKind:
                declaration.Members = element.Elements().Where(IsNamed("member")).Select(ParseMember).ToList();
                break;
            case EnumKind:
                declaration.EnumValues = element.Elements().Where(IsNamed("enumerator")).Select(value => new DdsEnumValueEditor
                {
                    Name = Attribute(value, "name"),
                    Value = Attribute(value, "value"),
                    DefaultLiteral = BooleanAttribute(value, "defaultLiteral"),
                    SourceXml = value.ToString(SaveOptions.DisableFormatting),
                }).ToList();
                break;
            case UnionKind:
                var discriminator = element.Elements().FirstOrDefault(IsNamed("discriminator"));
                if (discriminator != null)
                    declaration.DiscriminatorTypeName = EffectiveTypeName(discriminator);
                declaration.UnionCases = element.Elements().Where(IsNamed("case")).Select(unionCase => new DdsUnionCaseEditor
                {
                    Labels = string.Join(", ", unionCase.Elements().Where(IsNamed("caseDiscriminator")).Select(label => Attribute(label, "value"))),
                    Member = unionCase.Elements().FirstOrDefault(IsNamed("member")) is { } member ? ParseMember(member) : new DdsTypeMemberEditor(),
                    SourceXml = unionCase.ToString(SaveOptions.DisableFormatting),
                }).ToList();
                break;
            case TypedefKind:
                declaration.TypeName = EffectiveTypeName(element);
                declaration.StringMaxLength = Attribute(element, "stringMaxLength");
                declaration.SequenceMaxLength = Attribute(element, "sequenceMaxLength");
                declaration.ArrayDimensions = Attribute(element, "arrayDimensions");
                declaration.DefaultValue = Attribute(element, "default");
                declaration.MinValue = Attribute(element, "min");
                declaration.MaxValue = Attribute(element, "max");
                break;
            case ConstKind:
                declaration.TypeName = EffectiveTypeName(element);
                declaration.StringMaxLength = Attribute(element, "stringMaxLength");
                declaration.Value = Attribute(element, "value");
                break;
        }

        return declaration;
    }

    private static DdsTypeMemberEditor ParseMember(XElement element) => new()
    {
        Name = Attribute(element, "name"),
        TypeName = EffectiveTypeName(element),
        StringMaxLength = Attribute(element, "stringMaxLength"),
        SequenceMaxLength = Attribute(element, "sequenceMaxLength"),
        ArrayDimensions = Attribute(element, "arrayDimensions"),
        Key = BooleanAttribute(element, "key"),
        Optional = BooleanAttribute(element, "optional"),
        External = BooleanAttribute(element, "external"),
        MemberId = Attribute(element, "id"),
        DefaultValue = Attribute(element, "default"),
        MinValue = Attribute(element, "min"),
        MaxValue = Attribute(element, "max"),
        SourceXml = element.ToString(SaveOptions.DisableFormatting),
    };

    private static XElement BuildDeclaration(DdsTypeDeclarationEditor declaration)
    {
        var element = SourceOrNew(declaration.SourceXml, declaration.Kind);
        element.Name = declaration.Kind;
        SetAttribute(element, "name", declaration.Name.Trim());

        switch (declaration.Kind)
        {
            case StructKind:
                SetOptionalAttribute(element, "extensibility", declaration.Extensibility);
                SetOptionalAttribute(element, "autoid", declaration.AutoId);
                SetOptionalAttribute(element, "baseType", declaration.BaseType);
                SetBooleanAttribute(element, "nested", declaration.Nested);
                element.Elements().Where(IsNamed("member")).Remove();
                foreach (var member in declaration.Members)
                    element.Add(BuildMember(member, structMember: true));
                break;
            case EnumKind:
                SetOptionalAttribute(element, "extensibility", declaration.Extensibility);
                element.Elements().Where(IsNamed("enumerator")).Remove();
                foreach (var value in declaration.EnumValues)
                {
                    var valueElement = SourceOrNew(value.SourceXml, "enumerator");
                    valueElement.Name = "enumerator";
                    SetAttribute(valueElement, "name", value.Name.Trim());
                    SetOptionalAttribute(valueElement, "value", value.Value);
                    SetBooleanAttribute(valueElement, "defaultLiteral", value.DefaultLiteral);
                    element.Add(valueElement);
                }
                break;
            case UnionKind:
                SetOptionalAttribute(element, "extensibility", declaration.Extensibility);
                SetOptionalAttribute(element, "autoid", declaration.AutoId);
                element.Elements().Where(child => child.Name.LocalName is "discriminator" or "case").Remove();
                var discriminator = new XElement("discriminator");
                SetType(discriminator, declaration.DiscriminatorTypeName);
                element.Add(discriminator);
                foreach (var item in declaration.UnionCases)
                {
                    var caseElement = SourceOrNew(item.SourceXml, "case");
                    caseElement.Name = "case";
                    caseElement.Elements().Where(child => child.Name.LocalName is "caseDiscriminator" or "member").Remove();
                    foreach (var label in SplitList(item.Labels))
                        caseElement.Add(new XElement("caseDiscriminator", new XAttribute("value", label)));
                    caseElement.Add(BuildMember(item.Member, structMember: false));
                    element.Add(caseElement);
                }
                break;
            case TypedefKind:
                SetType(element, declaration.TypeName);
                SetOptionalAttribute(element, "stringMaxLength", declaration.StringMaxLength);
                SetOptionalAttribute(element, "sequenceMaxLength", declaration.SequenceMaxLength);
                SetOptionalAttribute(element, "arrayDimensions", declaration.ArrayDimensions);
                SetOptionalAttribute(element, "default", declaration.DefaultValue);
                SetOptionalAttribute(element, "min", declaration.MinValue);
                SetOptionalAttribute(element, "max", declaration.MaxValue);
                break;
            case ConstKind:
                SetType(element, declaration.TypeName);
                SetOptionalAttribute(element, "stringMaxLength", declaration.StringMaxLength);
                SetAttribute(element, "value", declaration.Value.Trim());
                break;
        }

        return element;
    }

    private static XElement BuildMember(DdsTypeMemberEditor member, bool structMember)
    {
        var element = SourceOrNew(member.SourceXml, "member");
        element.Name = "member";
        SetAttribute(element, "name", member.Name.Trim());
        SetType(element, member.TypeName);
        SetOptionalAttribute(element, "stringMaxLength", member.StringMaxLength);
        SetOptionalAttribute(element, "sequenceMaxLength", member.SequenceMaxLength);
        SetOptionalAttribute(element, "arrayDimensions", member.ArrayDimensions);
        SetBooleanAttribute(element, "external", member.External);
        SetOptionalAttribute(element, "id", member.MemberId);
        SetOptionalAttribute(element, "default", member.DefaultValue);
        SetOptionalAttribute(element, "min", member.MinValue);
        SetOptionalAttribute(element, "max", member.MaxValue);
        SetBooleanAttribute(element, "key", structMember && member.Key);
        SetBooleanAttribute(element, "optional", structMember && member.Optional);
        return element;
    }

    private static XElement EnsureModule(XElement types, string? modulePath)
    {
        var current = types;
        foreach (var segment in SplitScopedName(modulePath))
        {
            var next = current.Elements().FirstOrDefault(element =>
                element.Name.LocalName == "module" &&
                string.Equals(Attribute(element, "name"), segment, StringComparison.Ordinal));
            if (next == null)
            {
                next = new XElement("module", new XAttribute("name", segment));
                current.Add(next);
            }
            current = next;
        }
        return current;
    }

    private static void ValidateMembers(IEnumerable<DdsTypeMemberEditor> members, string owner, bool allowKeyAndOptional)
    {
        var list = members.ToList();
        var duplicate = list.GroupBy(member => member.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"'{owner}'에 중복 member가 있습니다: {duplicate.Key}");

        foreach (var member in list)
        {
            ValidateIdentifier(member.Name, $"'{owner}' member 이름");
            ValidateTypeName(member.TypeName, $"'{member.Name}' 타입");
            ValidateBounds(member.StringMaxLength, member.SequenceMaxLength, member.ArrayDimensions, member.Name);
            if (allowKeyAndOptional && member.Key && member.Optional)
                throw new InvalidOperationException($"member '{member.Name}'은 key와 optional을 동시에 사용할 수 없습니다.");
            if (!string.IsNullOrWhiteSpace(member.MemberId) &&
                (!uint.TryParse(member.MemberId, out var id) || id > 268435455))
                throw new InvalidOperationException($"member '{member.Name}'의 ID는 0~268435455 범위여야 합니다.");
        }
    }

    private static void ValidateEnumValues(DdsTypeDeclarationEditor declaration)
    {
        var duplicate = declaration.EnumValues.GroupBy(value => value.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"enum '{QualifiedName(declaration)}'에 중복 값이 있습니다: {duplicate.Key}");
        var numericValues = new HashSet<long>();
        foreach (var value in declaration.EnumValues)
        {
            ValidateIdentifier(value.Name, $"enum '{QualifiedName(declaration)}' 값");
            if (!string.IsNullOrWhiteSpace(value.Value))
            {
                if (!long.TryParse(value.Value, out var numeric))
                    throw new InvalidOperationException($"enum '{QualifiedName(declaration)}' 값 '{value.Value}'은 정수여야 합니다.");
                if (!numericValues.Add(numeric))
                    throw new InvalidOperationException($"enum '{QualifiedName(declaration)}'에 중복 숫자 값이 있습니다: {numeric}");
            }
        }
        if (declaration.EnumValues.Count(value => value.DefaultLiteral) > 1)
            throw new InvalidOperationException($"enum '{QualifiedName(declaration)}'의 기본 값은 하나만 지정할 수 있습니다.");
    }

    private static void ValidateBounds(string stringMax, string sequenceMax, string arrayDimensions, string owner)
    {
        ValidateBound(stringMax, allowUnbounded: true, $"'{owner}' string bound");
        ValidateBound(sequenceMax, allowUnbounded: true, $"'{owner}' sequence bound");
        foreach (var dimension in SplitList(arrayDimensions))
        {
            if (ulong.TryParse(dimension, out var numeric))
            {
                if (numeric is 0 or > uint.MaxValue)
                    throw new InvalidOperationException($"'{owner}' 배열 차원은 1~4294967295 범위여야 합니다.");
            }
            else
            {
                ValidateScopedName(dimension, $"'{owner}' 배열 차원 상수");
            }
        }
    }

    private static void ValidateBound(string value, bool allowUnbounded, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var trimmed = value.Trim();
        if (allowUnbounded && trimmed == "-1") return;
        if (int.TryParse(trimmed, out var numeric))
        {
            if (numeric < 0)
                throw new InvalidOperationException($"{label}은 -1 또는 0 이상의 값이어야 합니다.");
            return;
        }
        ValidateScopedName(trimmed, label);
    }

    private static void ValidateTypeName(string value, string label)
    {
        if (PrimitiveTypeNames.Contains(value.Trim())) return;
        ValidateScopedName(value, label);
    }

    private static void ValidateIdentifier(string value, string label)
    {
        if (!IdentifierRegex().IsMatch(value.Trim()))
            throw new InvalidOperationException($"{label} '{value}'은(는) 올바른 IDL 식별자가 아닙니다.");
    }

    private static void ValidateScopedName(string value, string label)
    {
        var segments = SplitScopedName(value);
        var normalized = value.Trim();
        if (normalized.StartsWith("::", StringComparison.Ordinal) || normalized.EndsWith("::", StringComparison.Ordinal) ||
            normalized.Contains("::::", StringComparison.Ordinal) || segments.Count == 0 ||
            segments.Any(segment => !IdentifierRegex().IsMatch(segment)))
            throw new InvalidOperationException($"{label} '{value}'은(는) 올바른 IDL 이름이 아닙니다.");
    }

    private static List<string> SplitScopedName(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split("::", StringSplitOptions.TrimEntries).ToList();

    private static IEnumerable<string> ReferencedTypes(DdsTypeDeclarationEditor declaration)
    {
        if (!string.IsNullOrWhiteSpace(declaration.BaseType)) yield return declaration.BaseType.Trim();
        if (declaration.Kind == UnionKind && !string.IsNullOrWhiteSpace(declaration.DiscriminatorTypeName)) yield return declaration.DiscriminatorTypeName.Trim();
        if (declaration.Kind is TypedefKind or ConstKind && !string.IsNullOrWhiteSpace(declaration.TypeName)) yield return declaration.TypeName.Trim();
        foreach (var member in declaration.Members.Concat(declaration.UnionCases.Select(item => item.Member)))
            if (!string.IsNullOrWhiteSpace(member.TypeName)) yield return member.TypeName.Trim();
    }

    private static void ValidateAliasCycles(IEnumerable<DdsTypeDeclarationEditor> declarations)
    {
        var aliases = declarations.Where(item => item.Kind == TypedefKind).ToDictionary(QualifiedName, StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases.Values)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = alias;
            while (true)
            {
                if (!visited.Add(QualifiedName(current))) throw new InvalidOperationException($"typedef 순환 참조가 있습니다: {QualifiedName(current)}");
                var target = current.TypeName.Trim();
                var relative = string.IsNullOrWhiteSpace(current.ModulePath) ? target : $"{current.ModulePath}::{target}";
                if (!aliases.TryGetValue(target, out var next) && !aliases.TryGetValue(relative, out next)) break;
                current = next;
            }
        }
    }

    private static bool IsIntegerDiscriminator(string typeName)
        => typeName.Contains("int", StringComparison.OrdinalIgnoreCase) || typeName is "byte" or "octet" or "short" or "long" or "unsignedShort" or "unsignedLong" or "longLong" or "unsignedLongLong";

    private static List<string> SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static XDocument? OwnerDocument(XElement element) => element.Document;
    private static XElement? FindTypes(XDocument document)
    {
        var matches = document.Root?.Name.LocalName == "types"
            ? new List<XElement> { document.Root }
            : document.Descendants().Where(IsNamed("types")).ToList();
        if (matches.Count > 1)
            throw new InvalidOperationException("RTI Connext 7.3 프로필에서는 <types> 요소를 하나만 사용할 수 있습니다.");
        return matches.SingleOrDefault();
    }

    private static Func<XElement, bool> IsNamed(string name)
        => element => element.Name.LocalName == name;

    private static string Attribute(XElement element, string name)
        => element.Attribute(name)?.Value?.Trim() ?? string.Empty;

    private static bool BooleanAttribute(XElement element, string name)
        => bool.TryParse(Attribute(element, name), out var result) && result;

    private static string EffectiveTypeName(XElement element)
        => Attribute(element, "type") == "nonBasic"
            ? Attribute(element, "nonBasicTypeName")
            : Attribute(element, "type");

    private static XElement SourceOrNew(string? sourceXml, string name)
    {
        if (!string.IsNullOrWhiteSpace(sourceXml))
        {
            try { return XElement.Parse(sourceXml, LoadOptions.PreserveWhitespace); }
            catch { /* 새 요소로 복구 */ }
        }
        return new XElement(name);
    }

    private static void SetType(XElement element, string typeName)
    {
        var normalized = typeName.Trim();
        if (PrimitiveTypeNames.Contains(normalized))
        {
            SetAttribute(element, "type", normalized);
            element.Attribute("nonBasicTypeName")?.Remove();
        }
        else
        {
            SetAttribute(element, "type", "nonBasic");
            SetAttribute(element, "nonBasicTypeName", normalized);
        }
    }

    private static void SetAttribute(XElement element, string name, string value)
        => element.SetAttributeValue(name, value);

    private static void SetOptionalAttribute(XElement element, string name, string? value)
        => element.SetAttributeValue(name, string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    private static void SetBooleanAttribute(XElement element, string name, bool value)
        => element.SetAttributeValue(name, value ? "true" : null);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}
