using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using Prometheus;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration
    .SetBasePath(builder.Environment.ContentRootPath +
                 "/Settings") // the path where the json file should be loaded from
    .AddJsonFile("gateway.json", optional: true);

builder.Services.AddOpenTelemetry()
    .WithMetrics(builder => builder
        // Metrics provider from OpenTelemetry
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddAspNetCoreInstrumentation()
        // Metrics provides by ASP.NET Core in .NET 8
        .AddMeter("Microsoft.AspNetCore.Hosting")
        .AddMeter("Microsoft.AspNetCore.Server.Kestrel")
        .AddPrometheusExporter()); // We use v1.7 because currently v1.8 has an issue with formatting.


builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        IssuerSigningKey = new SymmetricSecurityKey
            (Encoding.UTF8.GetBytes(builder.Configuration.GetValue<string>("JwtAuthSettings:AccessTokenSecret"))),
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true
    };
});

builder.Services.AddAuthorization(options =>
{
    // This will make it so authentication is always required, unless specified otherwise.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddCors(p => p.AddPolicy("corsSettings",
    policy =>
    {
        policy.WithOrigins("http://localhost:5173", "localhost:5173").AllowAnyMethod()
            .AllowAnyHeader().AllowCredentials();
    }));


builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    
    options.AddPolicy("fixed-by-user", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.Identity?.Name?.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(20)
            }));
    
    // The rate limiting per ip address is higher because this is more vunerable
    // to a ddos attack by anonymous users
    options.AddPolicy("fixed-by-ip", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString(),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromSeconds(60)
            }));
});


builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseCors("corsSettings");

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", async () => "Hello World");

app.MapPrometheusScrapingEndpoint().AllowAnonymous();

app.UseRateLimiter();

app.MapReverseProxy();


app.Run();


