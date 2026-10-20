using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Com.Api.Endpoints;
using Com.Api.Services;
using Com.Shared.Abstractions;
using Com.Shared.Contracts;
using Com.Shared.Data;
using Com.Shared.Domain;
using Com.Shared.Services;
using Com.Shared.Workers;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
var role = (cfg["APP_ROLE"] ?? "all").ToLowerInvariant();   // all | web | api
var isWeb = role is "web" or "all";

// ── 数据库：同一模型，两种提供程序 ──────────────────────────────
var provider = (cfg["DB_PROVIDER"] ?? "sqlite").ToLowerInvariant();
var cs = cfg.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(cs))
    cs = provider == "sqlserver" ? "" : "Data Source=com.db";

ComDbContext.SqliteCompatMode = provider != "sqlserver";
builder.Services.AddDbContext<ComDbContext>(o =>
{
    if (provider == "sqlserver") o.UseSqlServer(cs, s => s.EnableRetryOnFailure(3));
    else o.UseSqlite(cs);
});

// ── 平台服务：按配置选实现 ────────────────────────────────────
var storageMode = (cfg["STORAGE_MODE"] ?? "local").ToLowerInvariant();
if (storageMode == "azure") builder.Services.AddSingleton<IBlobStore, AzureBlobStore>();
else builder.Services.AddSingleton<IBlobStore, LocalBlobStore>();

var busMode = (cfg["BUS_MODE"] ?? "local").ToLowerInvariant();
if (busMode == "servicebus") builder.Services.AddSingleton<IJobQueue, ServiceBusJobQueue>();
else builder.Services.AddSingleton<IJobQueue, LocalJobQueue>();

builder.Services.AddHttpClient<IWorkflowBridge, LogicAppsBridge>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICacheStore, MemoryCacheStore>();
builder.Services.AddSingleton<IPdfRenderer, QuestPdfRenderer>();
builder.Services.AddScoped<INotifier, NotificationService>();
builder.Services.AddSingleton<IAuditMirror, CosmosAuditMirror>();
builder.Services.AddScoped<ContractWorkHandler>();
builder.Services.AddScoped<IntakeWorkHandler>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddHttpContextAccessor();

// ── 认证：Bearer JWT（前后端分域部署时无 Cookie 跨站问题）────────
var jwtKey = cfg["JWT_KEY"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
{
    jwtKey = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + Convert.ToBase64String(Guid.NewGuid().ToByteArray());
    Console.WriteLine("[WARN] JWT_KEY 未配置或过短，已使用进程级临时密钥（多实例下令牌不通用）");
}
JwtKeys.Key = Encoding.UTF8.GetBytes(jwtKey);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "com",
            ValidateAudience = true,
            ValidAudience = "com-portal",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(JwtKeys.Key),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "name",
            RoleClaimType = "role"
        };
    });
builder.Services.AddAuthorization();

// ── CORS：门户在另一个域名下，必须显式放行 ───────────────────
var portalOrigins = (cfg["PORTAL_ORIGINS"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (portalOrigins.Length > 0) p.WithOrigins(portalOrigins).AllowAnyHeader().AllowAnyMethod();
    else p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod();
}));

// ── 可观测性 ─────────────────────────────────────────────────
if (!string.IsNullOrWhiteSpace(cfg["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();

// ── 统一异常 → problem+json ──────────────────────────────────
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (InvalidTransitionException ex)
    {
        await WriteProblem(ctx, 409, "invalid_transition", ex.Message);
    }
    catch (ValidationProblemException ex)
    {
        await WriteProblem(ctx, 400, "validation_failed", ex.Message, ex.Errors);
    }
    catch (KeyNotFoundException ex)
    {
        await WriteProblem(ctx, 404, "not_found", ex.Message);
    }
    catch (UnauthorizedAccessException ex)
    {
        await WriteProblem(ctx, 404, "not_found", ex.Message); // 越权一律 404，避免探测
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "未处理异常 {Path}", ctx.Request.Path);
        await WriteProblem(ctx, 500, "internal_error", "服务端异常，请携带 traceId 联系管理员");
    }
});

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

if (isWeb)
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapGet("/config.json", (IConfiguration c) => Results.Json(new
    {
        apiBase = c["API_BASE_URL"] ?? "",
        environment = c["ENVIRONMENT_NAME"] ?? "local",
        region = c["AZURE_REGION"] ?? "local",
        version = c["APP_VERSION"] ?? "1.0.0",
        demoAutoLogin = (c["ENABLE_DEMO_AUTOLOGIN"] ?? "true") == "true",
        role
    }));
}

// 健康检查
app.MapGet("/healthz", () => Results.Json(new { status = "ok", ts = DateTimeOffset.UtcNow, role }));
// /readyz 会碰数据库，Web 角色没有数据库，直接给静态的 ready
if (role != "web")
{
    app.MapGet("/readyz", async (ComDbContext db) =>
    {
        try { await db.Database.ExecuteSqlRawAsync("SELECT 1"); return Results.Json(new { status = "ready" }); }
        catch (Exception ex) { return Results.Json(new { status = "not-ready", error = ex.Message }, statusCode: 503); }
    });
}

// Web 角色只做静态门户 + /config.json，不挂业务 API：
// 同一个发布包靠 APP_ROLE 分角色，若这里不加判断，Web 应用也会对外暴露
// 一套连着空库的 API，既浪费攻击面又容易在演示时被误访问。
if (role != "web")
{
    app.MapAuthEndpoints();
    app.MapMasterDataEndpoints();
    app.MapOrderEndpoints();
    app.MapApprovalEndpoints();
    app.MapContractEndpoints();
    app.MapIntakeEndpoints();
    app.MapOpsEndpoints();
    app.MapInternalEndpoints();
    app.MapNotificationEndpoints();
}

// ── 启动自检：建表、种子、容器 ───────────────────────────────
if (role != "web")
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ComDbContext>();
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        if (provider == "sqlite")
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

        var created = await db.Database.EnsureCreatedAsync();
        await Seeder.SeedAsync(db);
        await scope.ServiceProvider.GetRequiredService<IBlobStore>().EnsureContainersAsync();
        log.LogInformation("启动自检完成 provider={Provider} dbCreated={Created} role={Role}", provider, created, role);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "启动自检失败（服务仍启动，便于排障）");
    }
}

app.Run();

static async Task WriteProblem(HttpContext ctx, int status, string code, string message, object? errors = null)
{
    if (ctx.Response.HasStarted) return;
    ctx.Response.Clear();
    ctx.Response.StatusCode = status;
    ctx.Response.ContentType = "application/problem+json; charset=utf-8";
    var traceId = System.Diagnostics.Activity.Current?.Id ?? ctx.TraceIdentifier;
    await ctx.Response.WriteAsync(JsonSerializer.Serialize(
        new ApiError(code, message, traceId, errors),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
}

public static class JwtKeys
{
    public static byte[] Key { get; set; } = Array.Empty<byte>();
}

public class ValidationProblemException : Exception
{
    public object Errors { get; }
    public ValidationProblemException(string message, object errors) : base(message) => Errors = errors;
}

public partial class Program { }
