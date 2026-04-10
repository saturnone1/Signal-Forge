using GrpcWorkbench.Grpc;
using GrpcWorkbench.Hubs;
using GrpcWorkbench.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to support HTTP/1.1 and HTTP/2
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(lo =>
    {
        lo.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

// Add services to the container
builder.Services.AddGrpc();
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddRazorPages();

// Add gRPC Workbench services
builder.Services.AddSingleton<ISessionService, SessionService>();
builder.Services.AddSingleton<IGrpcChannelProvider, GrpcChannelProvider>();
builder.Services.AddSingleton<IProtoLoader, ProtoLoader>();
builder.Services.AddSingleton<IJsonMessageConverter, JsonMessageConverter>();
builder.Services.AddSingleton<IGrpcServiceClientFinder, GrpcServiceClientFinder>();
builder.Services.AddSingleton<IDynamicProtoCompiler, DynamicProtoCompiler>();
builder.Services.AddScoped<IUnaryGrpcService, UnaryGrpcService>();
builder.Services.AddScoped<IStreamingGrpcService, StreamingGrpcService>();
builder.Services.AddSingleton<IActiveStreamManager, ActiveStreamManager>();

// Add Cors
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", corsPolicyBuilder =>
    {
        corsPolicyBuilder
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline
app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseRouting();

// Map Razor Pages, Controllers, and SignalR hub
app.MapRazorPages();
app.MapControllers();
app.MapHub<GrpcWorkbenchHub>("/hubs/grpc-workbench");

app.Run();
