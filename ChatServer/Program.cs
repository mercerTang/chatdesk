using System.Text;
using ChatServer.Data;
using ChatServer.Hubs;
using ChatServer.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 日志：Serilog 控制台 + 文件（测试宿主可关闭文件落盘，避免多宿主争用同一文件）
var logConfiguration = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
    .Enrich.FromLogContext()
    .WriteTo.Console();

if (builder.Configuration.GetValue("Serilog:FileLogging", true))
{
    logConfiguration = logConfiguration.WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "log-.txt"),
        rollingInterval: RollingInterval.Day,
        shared: true);
}

Log.Logger = logConfiguration.CreateLogger();
builder.Host.UseSerilog();

// 数据库：未配置连接串时落在项目目录下的 chat.db
var configuredConnectionString = builder.Configuration.GetConnectionString("Default");
var rawConnectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
    ? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "chat.db")}"
    : configuredConnectionString;

// SQLite 的等锁超时（busy_timeout）来自连接上的 Default Timeout：每条请求连接都从连接串取默认值；
// PRAGMA busy_timeout 只作用于执行它的那一条连接，连接池里的其它连接不受影响。
var connectionString = rawConnectionString.Contains("Default Timeout", StringComparison.OrdinalIgnoreCase)
    ? rawConnectionString
    : new SqliteConnectionStringBuilder(rawConnectionString) { DefaultTimeout = 5 }.ToString();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<RetentionService>();
builder.Services.AddHostedService<RetentionHostedService>();
builder.Services.AddSingleton<AgentPresenceService>();
builder.Services.AddSingleton<JwtTokenService>();

builder.Services.AddSignalR();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// JWT 鉴权；SignalR 从 access_token 查询参数取令牌
var jwtKey = builder.Configuration["Jwt:Key"] ?? throw new InvalidOperationException("缺少 Jwt:Key 配置");
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ChatServer";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "ChatClient";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            NameClaimType = "name",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken)
                    && context.HttpContext.Request.Path.StartsWithSegments("/hubs/chat"))
                {
                    context.Token = accessToken;
                }

                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseSerilogRequestLogging();

// 全局异常兜底：统一 { error } 结构，不向客户端暴露堆栈
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            context.RequestServices.GetRequiredService<ILogger<Program>>()
                .LogError(exception, "未处理异常：{Method} {Path}", context.Request.Method, context.Request.Path);
        }

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new
        {
            error = statusCode >= StatusCodes.Status500InternalServerError
                ? "服务器内部错误，请稍后重试"
                : exception?.Message ?? "请求无效"
        });
    });
});

app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

app.MapGet("/health", () => Results.Ok(new
{
    status = "OK",
    message = "ChatServer is running",
    time = DateTimeOffset.UtcNow
}));

// 建库 + 种子 + 在线标记复位
await DatabaseInitializer.InitializeAsync(app.Services);

app.Run();

/// <summary>供集成测试的 WebApplicationFactory 引用。</summary>
public partial class Program
{
}
