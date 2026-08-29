using ASAP.Dds;
using ASAP.Services;
using MudBlazor.Services;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options => options.DetailedErrors = builder.Environment.IsDevelopment());
builder.Services.AddMudServices();

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

app.MapGet("/health", () => Results.Ok(new { status = "connected" }));
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
