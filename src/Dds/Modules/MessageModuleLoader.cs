using DdsAmbassador.DDSClient;

namespace ASAP.Dds;

/// <summary>신뢰하는 DLL과 계약을 검증하고 Core·RTI를 공유하는 로딩 문맥을 관리한다.</summary>
internal static class MessageModuleLoader
{
    // 신뢰하는 빌드 산출물만 배치한다. DLL 로드는 코드 실행이므로 브라우저 업로드는 허용하지 않는다.
    public static BuiltMessageModule[] Load()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "modules");
        var external = Environment.GetEnvironmentVariable("SIGNAL_FORGE_MODULE_DIRECTORY");
        external = string.IsNullOrWhiteSpace(external) ? null : Path.GetFullPath(external);
        var directories = new[] { bundled, external }.Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!)).Distinct(StringComparer.Ordinal).ToArray();
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "DdsAmbassador.Messages.*.dll"))
                assemblies[Path.GetFileName(path)] = path;
        }
        var result = new List<BuiltMessageModule>();
        foreach (var path in assemblies.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value))
        {
            // 배포 폴더와 외부 폴더를 같은 규칙으로 읽어 기존 deps.json 참조보다 교체 DLL을 우선한다.
            // 메시지 어셈블리는 격리하고 Core와 RTI 런타임 타입은 공유한다.
            var assembly = new MessageModuleLoadContext(path).LoadFromAssemblyPath(path);
            var accessors = assembly.GetExportedTypes().SelectMany(type => type.GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                .Where(property => property.Name == "Module" && property.PropertyType == typeof(DdsMessageModule)).ToArray();
            if (accessors.Length != 1) throw new InvalidOperationException("메시지 모듈 진입점이 하나여야 합니다: " + path);
            var module = (DdsMessageModule?)accessors[0].GetValue(null)
                ?? throw new InvalidOperationException("메시지 모듈 정보가 없습니다: " + path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(module.Name, @"^[A-Za-z][A-Za-z0-9_]*$"))
                throw new InvalidOperationException("잘못된 메시지 모듈 이름입니다.");
            var contractPath = Path.Combine(Path.GetDirectoryName(path)!, module.Name + ".xml");
            if (!File.Exists(contractPath)) throw new InvalidOperationException("모듈 계약 XML이 없습니다: " + contractPath);
            if (result.Any(item => string.Equals(item.Module.Name, module.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("중복 메시지 모듈: " + module.Name);
            result.Add(new BuiltMessageModule(module, File.ReadAllText(contractPath)));
        }
        if (result.Count == 0) throw new InvalidOperationException("modules 폴더에 메시지 모듈 DLL과 계약 XML을 배치하세요.");
        return result.ToArray();
    }

    private sealed class MessageModuleLoadContext(string path) : System.Runtime.Loader.AssemblyLoadContext
    {
        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName name)
        {
            if (name.Name == typeof(DdsMessageModule).Assembly.GetName().Name) return typeof(DdsMessageModule).Assembly;
            if (name.Name?.StartsWith("Rti.", StringComparison.Ordinal) == true || name.Name?.StartsWith("Omg.", StringComparison.Ordinal) == true)
                return Default.LoadFromAssemblyName(name);
            var dependency = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
            return File.Exists(dependency) ? LoadFromAssemblyPath(dependency) : null;
        }
    }


}
