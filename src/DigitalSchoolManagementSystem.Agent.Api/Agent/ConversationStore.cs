using DigitalSchoolManagementSystem.Agent.Api.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace DigitalSchoolManagementSystem.Agent.Api.Agent
{
    public class Conversation
    {
        // Serializes turns within one conversation (e.g. a double-clicked Send).
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public List<ChatMessage> History { get; } = [];
    }

    // In-memory chat history, keyed by user + conversation id so one student can never read
    // another's conversation. Idle conversations expire; a restart forgets everything.
    public class ConversationStore(IMemoryCache cache, IOptions<AgentOptions> options)
    {
        private readonly AgentOptions _options = options.Value;

        public (string Id, Conversation Conversation) GetOrCreate(int userId, string? conversationId)
        {
            if (!string.IsNullOrWhiteSpace(conversationId) && Guid.TryParse(conversationId, out var parsed)
                && cache.TryGetValue(Key(userId, parsed.ToString("N")), out Conversation? existing) && existing is not null)
            {
                Touch(userId, parsed.ToString("N"), existing);
                return (parsed.ToString("N"), existing);
            }

            var id = Guid.NewGuid().ToString("N");
            var conversation = new Conversation();
            Touch(userId, id, conversation);
            return (id, conversation);
        }

        public void Remove(int userId, string conversationId)
        {
            if (Guid.TryParse(conversationId, out var parsed))
                cache.Remove(Key(userId, parsed.ToString("N")));
        }

        // Keeps only the most recent messages; history holds user/assistant text only (no tool
        // messages), so trimming can never orphan a tool call from its result.
        public void Append(Conversation conversation, params ChatMessage[] messages)
        {
            conversation.History.AddRange(messages);
            var overflow = conversation.History.Count - _options.MaxHistoryMessages;
            if (overflow > 0)
                conversation.History.RemoveRange(0, overflow);
        }

        private void Touch(int userId, string id, Conversation conversation) =>
            cache.Set(Key(userId, id), conversation, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(_options.ConversationIdleMinutes),
                Size = 1
            });

        private static string Key(int userId, string id) => $"conversation:{userId}:{id}";
    }
}
