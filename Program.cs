using ASAP.Dds;
using ASAP.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

builder.Services.AddScoped<UiStateService>();

builder.Services.AddSingleton<DdsParticipantHostFactory>();
builder.Services.AddSingleton<IDdsSessionService, DdsSessionService>();
builder.Services.AddSingleton<DdsStateService>();
builder.Services.AddSingleton<DdsTriggerService>();

// ── App ──────────────────────────────────────────────────────────────────────
var app = builder.Build();

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
