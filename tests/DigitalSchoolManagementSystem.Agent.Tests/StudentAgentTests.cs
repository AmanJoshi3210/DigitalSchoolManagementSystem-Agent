using System.Text.Json;
using DigitalSchoolManagementSystem.Agent.Api.Agent;
using DigitalSchoolManagementSystem.Agent.Api.Knowledge;
using DigitalSchoolManagementSystem.Agent.Api.Llm;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using static DigitalSchoolManagementSystem.Agent.Api.Backend.EnumLabels;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    // Runs the real tool-calling pipeline (FunctionInvokingChatClient + StudentPortalTools)
    // over a scripted fake model and a fake backend.
    public class StudentAgentTests
    {
        private readonly FakeBackend _backend = new();
        private readonly AgentOptions _options = new() { MaxHistoryMessages = 4 };

        private (StudentAgent Agent, FakeChatClient Model) CreateAgent(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script)
        {
            var model = new FakeChatClient(script);
            var options = MsOptions.Create(_options);
            var agent = new StudentAgent(
                LlmProviderFactory.WithAgentPipeline(model, _options),
                new ConversationStore(new MemoryCache(new MemoryCacheOptions()), options),
                _backend,
                KnowledgeBase.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Knowledge")),
                new FixedTimeProvider(TestData.Today),
                options,
                NullLogger<StudentAgent>.Instance);
            return (agent, model);
        }

        private static string LastToolResult(IReadOnlyList<ChatMessage> messages) =>
            JsonSerializer.Serialize(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Last().Result);

        [Fact]
        public async Task Checklist_tool_runs_against_student_data_and_returns_actions()
        {
            string? toolResult = null;
            var (agent, model) = CreateAgent(
                _ => FakeChatClient.CallTool("get_application_checklist", new() { ["programId"] = 1 }),
                messages =>
                {
                    toolResult = LastToolResult(messages);
                    return FakeChatClient.Text("Here is your checklist.");
                });

            var result = await agent.ChatAsync(42, "Riya", null, "How do I apply to BSc?", CancellationToken.None);

            Assert.Equal("Here is your checklist.", result.Reply);
            Assert.Contains("\"canApplyNow\":true", toolResult);
            Assert.Contains("Missing", toolResult);
            Assert.Contains(result.Actions, a => a.Route == "/student/documents");
            Assert.Contains(result.Actions, a => a.Route == "/student/programs/1" && a.Label == "Open BSc Computer Science");
            Assert.Equal(2, model.Calls.Count);
        }

        [Fact]
        public async Task System_prompt_contains_guide_name_and_date_and_all_tools_are_offered()
        {
            var (agent, model) = CreateAgent(_ => FakeChatClient.Text("Hi!"));

            await agent.ChatAsync(42, "Riya", null, "hello", CancellationToken.None);

            var system = model.Calls[0][0];
            Assert.Equal(ChatRole.System, system.Role);
            Assert.Contains("named Riya", system.Text);
            Assert.Contains("2026-10-08", system.Text);
            Assert.Contains("Upload Document", system.Text);

            var toolNames = model.Options[0]!.Tools!.Select(t => t.Name).ToList();
            Assert.Equal(
                ["get_my_profile", "list_programs", "get_program_details", "get_my_applications", "get_my_documents", "get_required_documents", "get_application_checklist"],
                toolNames);
        }

        [Fact]
        public async Task Conversation_history_is_kept_per_conversation_without_tool_messages()
        {
            var (agent, model) = CreateAgent(
                _ => FakeChatClient.CallTool("get_my_documents"),
                _ => FakeChatClient.Text("You have no documents yet."),
                _ => FakeChatClient.Text("Upload it from Documents."));

            var first = await agent.ChatAsync(42, null, null, "What did I upload?", CancellationToken.None);
            await agent.ChatAsync(42, null, first.ConversationId, "Where do I upload?", CancellationToken.None);

            var secondTurnPrompt = model.Calls[2];
            Assert.Equal(
                [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User],
                secondTurnPrompt.Select(m => m.Role));
            Assert.Equal("You have no documents yet.", secondTurnPrompt[2].Text);
        }

        [Fact]
        public async Task Another_users_conversation_id_starts_a_fresh_conversation()
        {
            var (agent, model) = CreateAgent(_ => FakeChatClient.Text("one"), _ => FakeChatClient.Text("two"));

            var mine = await agent.ChatAsync(42, null, null, "hi", CancellationToken.None);
            var theirs = await agent.ChatAsync(99, null, mine.ConversationId, "hi", CancellationToken.None);

            Assert.NotEqual(mine.ConversationId, theirs.ConversationId);
            Assert.Equal(2, model.Calls[1].Count); // system + user only
        }

        [Fact]
        public async Task History_is_trimmed_to_the_configured_size()
        {
            var (agent, model) = CreateAgent(Enumerable.Range(0, 4).Select(i => (Func<IReadOnlyList<ChatMessage>, ChatResponse>)(_ => FakeChatClient.Text($"a{i}"))).ToArray());

            string? id = null;
            for (var i = 0; i < 4; i++)
                id = (await agent.ChatAsync(42, null, id, $"q{i}", CancellationToken.None)).ConversationId;

            var lastPrompt = model.Calls[3];
            Assert.Equal(1 + 4 + 1, lastPrompt.Count); // system + 4 history + new question
            Assert.Equal("q1", lastPrompt[1].Text);
        }

        [Fact]
        public async Task Backend_failure_becomes_a_tool_error_not_a_crash()
        {
            _backend.Fail = true;
            string? toolResult = null;
            var (agent, _) = CreateAgent(
                _ => FakeChatClient.CallTool("get_my_profile"),
                messages =>
                {
                    toolResult = LastToolResult(messages);
                    return FakeChatClient.Text("Portal data is unavailable, please retry.");
                });

            var result = await agent.ChatAsync(42, null, null, "Is my education level set?", CancellationToken.None);

            Assert.Contains("error", toolResult);
            Assert.Equal("Portal data is unavailable, please retry.", result.Reply);
        }

        [Fact]
        public async Task Required_documents_tool_uses_the_programs_level()
        {
            _backend.Programs = [TestData.Program(id: 5, name: "MSc Data Science", level: 2)];
            string? toolResult = null;
            var (agent, _) = CreateAgent(
                _ => FakeChatClient.CallTool("get_required_documents", new() { ["programId"] = 5 }),
                messages =>
                {
                    toolResult = LastToolResult(messages);
                    return FakeChatClient.Text("ok");
                });

            await agent.ChatAsync(42, null, null, "Which documents for MSc?", CancellationToken.None);

            Assert.Contains("Master", toolResult);
            Assert.Contains(DocumentType[DocumentTypes.Certificate], toolResult);
        }

        [Fact]
        public async Task Empty_model_reply_gets_a_safe_fallback()
        {
            var (agent, _) = CreateAgent(_ => FakeChatClient.Text(""));

            var result = await agent.ChatAsync(42, null, null, "??", CancellationToken.None);

            Assert.Contains("/student/messages", result.Reply);
        }
    }
}
