using System.Diagnostics;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using DigitalSchoolManagementSystem.Agent.Api.Knowledge;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using DigitalSchoolManagementSystem.Agent.Api.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace DigitalSchoolManagementSystem.Agent.Api.Agent
{
    public record ChatTurnResult(string ConversationId, string Reply, IReadOnlyList<PortalAction> Actions);

    public class StudentAgent(
        IChatClient chatClient,
        ConversationStore conversations,
        IDsmsBackend backend,
        KnowledgeBase knowledge,
        TimeProvider timeProvider,
        IOptions<AgentOptions> options,
        ILogger<StudentAgent> logger)
    {
        private const int MaxActionsPerReply = 4;
        private const string EmptyReplyFallback =
            "Sorry, I couldn't put an answer together for that. Could you rephrase, or raise a Query with staff in [Messages](/student/messages)?";

        private readonly AgentOptions _options = options.Value;

        public async Task<ChatTurnResult> ChatAsync(int userId, string? studentFirstName, string? conversationId, string message, CancellationToken cancellationToken)
        {
            var (id, conversation) = conversations.GetOrCreate(userId, conversationId);
            var tools = new StudentPortalTools(backend, knowledge, timeProvider);

            await conversation.Gate.WaitAsync(cancellationToken);
            try
            {
                var userMessage = new ChatMessage(ChatRole.User, message);
                List<ChatMessage> prompt =
                [
                    new(ChatRole.System, SystemPrompt.Build(knowledge.PortalGuide, studentFirstName, timeProvider.GetUtcNow().UtcDateTime)),
                    .. conversation.History,
                    userMessage
                ];

                var chatOptions = new ChatOptions
                {
                    Temperature = _options.Temperature,
                    MaxOutputTokens = _options.MaxOutputTokens,
                    Tools = tools.AsAITools(),
                    ToolMode = ChatToolMode.Auto
                };

                var stopwatch = Stopwatch.StartNew();
                var response = await chatClient.GetResponseAsync(prompt, chatOptions, cancellationToken);
                stopwatch.Stop();

                var reply = string.IsNullOrWhiteSpace(response.Text) ? EmptyReplyFallback : response.Text.Trim();

                // Metadata only - never log what the student wrote or what the model answered.
                logger.LogInformation(
                    "Agent turn for user {UserId}: provider {Provider}, {ElapsedMs} ms, tools [{Tools}], tokens in/out {InputTokens}/{OutputTokens}",
                    userId, _options.Provider, stopwatch.ElapsedMilliseconds, string.Join(", ", tools.ToolsCalled),
                    response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount);

                conversations.Append(conversation, userMessage, new ChatMessage(ChatRole.Assistant, reply));

                return new ChatTurnResult(id, reply, tools.Actions.Take(MaxActionsPerReply).ToList());
            }
            finally
            {
                conversation.Gate.Release();
            }
        }

        public void Reset(int userId, string conversationId) => conversations.Remove(userId, conversationId);
    }
}
