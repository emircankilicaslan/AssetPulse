using System.Data;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AssetPulse.Api.Data;
using AssetPulse.Api.Endpoints;
using AssetPulse.Api.Security;
using AssetPulse.Api.Services;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, lc) => lc
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", Serilog.Events.LogEventLevel.Error)
    .WriteTo.Console());

var config = builder.Configuration;
var jwtSecret = config["Jwt:Secret"];
if (string.IsNullOrEmpty(jwtSecret) || jwtSecret.Length < 32)
    throw new InvalidOperationException("Jwt:Secret tanımlı olmalı ve en az 32 karakter olmalıdır.");

builder.Services.Configure<PulseOptions>(config.GetSection("AssetPulse"));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
});
builder.Services.AddProblemDetails();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<AssetStore>();
builder.Services.AddScoped<AlertStore>();
builder.Services.AddScoped<SeriesStore>();
builder.Services.AddScoped<UserStore>();
builder.Services.AddSingleton<ReadingRetentionStore>();
builder.Services.AddHostedService<ReadingRetentionService>();
builder.Services.AddScoped<IngestionService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = TokenService.Issuer,
            ValidateAudience = true, ValidAudience = TokenService.Issuer,
            ValidateLifetime = true, RequireExpirationTime = true,
            ValidateIssuerSigningKey = true, IssuerSigningKey = TokenService.SigningKey(jwtSecret),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = "name", RoleClaimType = "role",
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy("AdminOnly", p => p.RequireRole("Admin"));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "bilinmiyor",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var swaggerEnabled = config.GetValue("Swagger:Enabled", true);
if (swaggerEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(o =>
    {
        o.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "AssetPulse API",
            Version = "v1",
            Description = "Altyapı varlıkları için ölçüm toplama, alarm ve sağlık izleme servisi.",
        });
        o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
            Description = "POST /api/auth/login ile alınan token.",
        });
        o.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
        });
    });
}

var app = builder.Build();

if (config.GetValue("Database:MigrateOnStartup", true))
    Migrator.Run(config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default tanımlı değil."), app.Logger);
await Seeder.RunAsync(app.Services, config);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();

// Swagger arayüzü ve belgesi kimlik doğrulamasından önce sunulur; API uç noktaları ise korumalı kalır.
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.DocumentTitle = "AssetPulse API");
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (swaggerEnabled)
    app.MapGet("/", () => Results.Redirect("/swagger")).AllowAnonymous().ExcludeFromDescription();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous().ExcludeFromDescription();
app.MapGet("/health/ready", async (Db db, CancellationToken ct) =>
{
    try
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct));
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "database-unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).AllowAnonymous().ExcludeFromDescription();

AuthEndpoints.Map(app);
AssetEndpoints.Map(app);
ReadingEndpoints.Map(app);
AlertEndpoints.Map(app);

app.Run();

public partial class Program { }
