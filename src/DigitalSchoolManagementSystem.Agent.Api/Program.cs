using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using DigitalSchoolManagementSystem.Agent.Api.Agent;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using DigitalSchoolManagementSystem.Agent.Api.Endpoints;
using DigitalSchoolManagementSystem.Agent.Api.Knowledge;
using DigitalSchoolManagementSystem.Agent.Api.Llm;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Configuration lives in settings.json (not appsettings.json). Rebuild the source list so the
// precedence is: settings.json < settings.{Environment}.json < user-secrets (Development)
// < environment variables < command line. Secrets (API keys, Jwt:Key) belong in the last three.
builder.Configuration.Sources.Clear();
builder.Configuration
    .AddEnvironmentVariables("DOTNET_")
    .AddEnvironmentVariables("ASPNETCORE_")
    .AddJsonFile("settings.json", optional: false, reloadOnChange: false)
    .AddJsonFile($"settings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false);
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration
    .AddEnvironmentVariables()
    .AddCommandLine(args);

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<ProvidersOptions>(builder.Configuration.GetSection(ProvidersOptions.SectionName));
builder.Services.Configure<BackendOptions>(builder.Configuration.GetSection(BackendOptions.SectionName));

var agentOptions = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
var providersOptions = builder.Configuration.GetSection(ProvidersOptions.SectionName).Get<ProvidersOptions>() ?? new ProvidersOptions();
var backendOptions = builder.Configuration.GetSection(BackendOptions.SectionName).Get<BackendOptions>() ?? new BackendOptions();
var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

// Without Jwt:Key nothing can be authenticated - refuse to start.
if (string.IsNullOrWhiteSpace(jwtOptions.Key))
    throw new InvalidOperationException("Jwt:Key is not configured. It must be the same key the DSMS API uses (set Jwt__Key).");

// A missing LLM key is not fatal: the service runs "unconfigured" (health says so, chat returns 503)
// so the rest of a container stack isn't taken down by a restart loop.
string? llmProblem = null;
try
{
    LlmProviderFactory.ResolveSettings(agentOptions, providersOptions);
}
catch (InvalidOperationException ex)
{
    llmProblem = ex.Message;
}
builder.Services.AddSingleton(new AssistantStatus(llmProblem));

// --- LLM ---------------------------------------------------------------------------------
if (llmProblem is null)
{
    builder.Services.AddSingleton<IChatClient>(sp =>
        LlmProviderFactory.WithAgentPipeline(
            LlmProviderFactory.CreateInnerClient(agentOptions, providersOptions),
            agentOptions,
            sp.GetRequiredService<ILoggerFactory>()));
}

// --- Knowledge, agent, backend -------------------------------------------------------------
builder.Services.AddSingleton(KnowledgeBase.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Knowledge")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache(o => o.SizeLimit = 10_000);
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddScoped<StudentAgent>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient<IDsmsBackend, DsmsApiClient>(client =>
{
    // Trailing slash so relative paths ("students/me") append to /api instead of replacing it.
    client.BaseAddress = new Uri(backendOptions.BaseUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// --- Auth: validate the same JWT the DSMS API issues ------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

// --- Per-student rate limit (LLM calls cost money) -----------------------------------------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "You're sending messages too quickly. Please wait a minute and try again." }, cancellationToken);

    options.AddPolicy(ChatEndpoints.RateLimitPolicy, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = agentOptions.RequestsPerMinutePerUser,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (corsOrigins.Length > 0)
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
        else
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    });
});

var app = builder.Build();

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAgentEndpoints();

if (llmProblem is null)
{
    app.Logger.LogInformation("DSMS Student Assistant using provider {Provider} (model {Model}), backend {Backend}",
        agentOptions.Provider, providersOptions.For(agentOptions.Provider).Model, backendOptions.BaseUrl);
}
else
{
    app.Logger.LogWarning("DSMS Student Assistant is UNCONFIGURED - chat will return 503. {Problem}", llmProblem);
}

app.Run();

public partial class Program;
