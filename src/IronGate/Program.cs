using IronGate.Extensions;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddIronGateAuth(builder.Configuration)
    .AddIronGateReverseProxy(builder.Configuration);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", service = "IronGate" }));

app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy();

app.Run();

public partial class Program;