using ASAP.Dds;
using ASAP.Services;
using Microsoft.AspNetCore.DataProtection;
using MudBlazor.Services;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddMudServices();
// Cookies are scoped by host/path, not TCP port. Independent instances must not
// overwrite one another's antiforgery cookie when opened on localhost together.
var instanceId = builder.Configuration["SignalForge:InstanceId"]
    ?? $"{Environment.MachineName}:{builder.Configuration["urls"]}:{builder.Environment.ContentRootPath}";
var instanceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId)))[..16];
builder.Services.AddAntiforgery(options => options.Cookie.Name = $".SignalForge.Antiforgery.{instanceKey}");
builder.Services.AddDataProtection()
    .SetApplicationName($"SignalForge.{instanceKey}")
    .PersistKeysToFileSystem(new DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, "data", "data-protection-keys")));

builder.Services.AddScoped<UiStateService>();
builder.Services.AddSingleton<DdsProfileService>();

builder.Services.AddSingleton<DdsParticipantHostFactory>();
builder.Services.AddSingleton<IDdsSessionService, DdsSessionService>();
builder.Services.AddSingleton<DdsStateService>();
builder.Services.AddSingleton<DdsTriggerService>();

// ── App ──────────────────────────────────────────────────────────────────────
var app = builder.Build();

var accessUser = builder.Configuration["AccessControl:Username"];
var accessPassword = builder.Configuration["AccessControl:Password"];
if (!string.IsNullOrWhiteSpace(accessUser) && !string.IsNullOrWhiteSpace(accessPassword))
{
    app.Use(async (context, next) =>
    {
        if (context.Request.Path == "/health") { await next(); return; }
        var encoded = context.Request.Headers.Authorization.ToString();
        var valid = false;
        if (encoded.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var supplied = Encoding.UTF8.GetString(Convert.FromBase64String(encoded[6..])).Split(':', 2);
                valid = supplied.Length == 2 && FixedEquals(supplied[0], accessUser) && FixedEquals(supplied[1], accessPassword);
            }
            catch (FormatException) { }
        }
        if (!valid)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Signal Forge\"";
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next();
    });
}

var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".ttf"] = "font/ttf";
provider.Mappings[".otf"] = "font/otf";
provider.Mappings[".woff"] = "font/woff";
provider.Mappings[".woff2"] = "font/woff2";
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions { ContentTypeProvider = provider });

app.UseRouting();
app.UseAntiforgery();

// 웹 서버가 살아 있는 것만으로 DDS 모듈 준비가 완료되었다고 표시하지 않는다.
// 모듈 등록부·임베디드 계약 초기화가 실패하면 시작 로그에 원인을 남기고 기동을 중단한다.
try
{
    _ = BuiltMessageRegistry.Modules;
}
catch (Exception error)
{
    app.Logger.LogCritical(error, "빌드된 DDS 메시지 모듈 초기화 실패");
    throw;
}
app.MapGet("/health", () => Results.Ok(new { status = "ready", messageModules = BuiltMessageRegistry.Modules.Count }));
app.MapRazorComponents<ASAP.Components.App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation("Signal Forge DDS workbench started");

app.Run();

static bool FixedEquals(string left, string right)
{
    var leftBytes = SHA256.HashData(Encoding.UTF8.GetBytes(left));
    var rightBytes = SHA256.HashData(Encoding.UTF8.GetBytes(right));
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}
