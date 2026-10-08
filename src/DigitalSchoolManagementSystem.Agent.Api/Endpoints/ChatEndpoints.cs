using System.Security.Claims;
using DigitalSchoolManagementSystem.Agent.Api.Agent;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using DigitalSchoolManagementSystem.Agent.Api.Tools;
using Microsoft.Extensions.Options;

namespace DigitalSchoolManagementSystem.Agent.Api.Endpoints
{
    public record ChatRequest(string? ConversationId, string? Message);

    public record ChatResponse(string ConversationId, string Reply, IReadOnlyList<PortalAction> Actions);

    public static class ChatEndpoints
    {
        public const string RateLimitPolicy = "per-student";

        public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/agent");

            group.MapGet("/health", (AssistantStatus status, IOptions<AgentOptions> agent, IOptions<ProvidersOptions> providers) => Results.Ok(new
            {
                status = status.IsConfigured ? "ok" : "unconfigured",
                provider = agent.Value.Provider.ToString(),
                model = providers.Value.For(agent.Value.Provider).Model,
                detail = status.IsConfigured ? null : $"No API key configured for {agent.Value.Provider}."
            })).AllowAnonymous();

            group.MapPost("/chat", ChatAsync)
                .RequireAuthorization(policy => policy.RequireRole("Student"))
                .RequireRateLimiting(RateLimitPolicy);

            group.MapDelete("/chat/{conversationId}", (string conversationId, ClaimsPrincipal user, ConversationStore conversations) =>
            {
                conversations.Remove(CurrentUserId(user), conversationId);
                return Results.NoContent();
            }).RequireAuthorization(policy => policy.RequireRole("Student"));

            return app;
        }

        private static async Task<IResult> ChatAsync(
            ChatRequest request,
            ClaimsPrincipal user,
            AssistantStatus status,
            IOptions<AgentOptions> options,
            ILoggerFactory loggerFactory,
            HttpContext httpContext)
        {
            if (!status.IsConfigured)
            {
                return Results.Json(
                    new { message = "The assistant isn't set up yet. Please try again later or contact staff in Messages." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Resolved only once configured - StudentAgent needs the IChatClient, which isn't registered otherwise.
            var agent = httpContext.RequestServices.GetRequiredService<StudentAgent>();
            var settings = options.Value;
            var message = request.Message?.Trim();

            if (string.IsNullOrEmpty(message))
                return Results.BadRequest(new { message = "Please type a question." });

            if (message.Length > settings.MaxUserMessageChars)
                return Results.BadRequest(new { message = $"Please keep your question under {settings.MaxUserMessageChars} characters." });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));

            try
            {
                var result = await agent.ChatAsync(
                    CurrentUserId(user),
                    user.FindFirstValue(ClaimTypes.GivenName),
                    request.ConversationId,
                    message,
                    timeout.Token);

                return Results.Ok(new ChatResponse(result.ConversationId, result.Reply, result.Actions));
            }
            catch (OperationCanceledException) when (!httpContext.RequestAborted.IsCancellationRequested)
            {
                return Results.Json(new { message = "The assistant took too long to answer. Please try again." }, statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Provider/SDK failures (bad key, quota, outage). Log the type and message only, not the conversation.
                loggerFactory.CreateLogger("DigitalSchoolManagementSystem.Agent.Chat")
                    .LogError("LLM provider {Provider} failed: {ErrorType}: {Error}", settings.Provider, ex.GetType().Name, ex.Message);
                return Results.Json(new { message = "The assistant is unavailable right now. Please try again in a moment." }, statusCode: StatusCodes.Status502BadGateway);
            }
        }

        private static int CurrentUserId(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }
}
