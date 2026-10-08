using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using DigitalSchoolManagementSystem.Agent.Api.Llm;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    public class AgentApiFactory : WebApplicationFactory<Program>
    {
        public FakeChatClient Model { get; set; } = new(_ => FakeChatClient.Text("Go to **Documents** and click **Upload Document**."));

        public AgentApiFactory()
        {
            // Program reads configuration eagerly (fail-fast checks), so supply it the way production
            // does - through environment variables, which take precedence over settings.json.
            Environment.SetEnvironmentVariable("Jwt__Key", TestJwt.Key);
            Environment.SetEnvironmentVariable("Providers__Gemini__ApiKey", "test-key");
            Environment.SetEnvironmentVariable("Agent__Provider", "Gemini");
        }

        protected override void Dispose(bool disposing)
        {
            Environment.SetEnvironmentVariable("Providers__Gemini__ApiKey", null);
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IChatClient>(_ => LlmProviderFactory.WithAgentPipeline(new ForwardingChatClient(() => Model), new AgentOptions()));
                services.AddSingleton<IDsmsBackend, FakeBackend>();
            });
        }

        // The IChatClient singleton is built once per host; forward to whatever script the current test set.
        private sealed class ForwardingChatClient(Func<IChatClient> current) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                current().GetResponseAsync(messages, options, cancellationToken);

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
                current().GetStreamingResponseAsync(messages, options, cancellationToken);

            public object? GetService(Type serviceType, object? serviceKey = null) => current().GetService(serviceType, serviceKey);

            public void Dispose() { }
        }
    }

    // Endpoint test classes configure the host through process-wide environment variables,
    // so they must not run in parallel with each other.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class AgentApiCollection
    {
        public const string Name = "Agent API host";
    }

    [Collection(AgentApiCollection.Name)]
    public class ChatEndpointTests(AgentApiFactory factory) : IClassFixture<AgentApiFactory>
    {
        private HttpClient Client(string? token)
        {
            var client = factory.CreateClient();
            if (token is not null)
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client;
        }

        private record ChatResponseBody(string ConversationId, string Reply, List<PortalActionBody> Actions);
        private record PortalActionBody(string Label, string Route);

        [Fact]
        public async Task Health_is_public_and_reports_provider_without_secrets()
        {
            var body = await Client(null).GetStringAsync("/agent/health");

            Assert.Contains("\"provider\":\"Gemini\"", body);
            Assert.DoesNotContain("test-key", body);
        }

        [Fact]
        public async Task Chat_requires_a_token()
        {
            var response = await Client(null).PostAsJsonAsync("/agent/chat", new { message = "hi" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Chat_rejects_tokens_signed_with_another_key()
        {
            var forged = TestJwt.Create(key: "a-completely-different-signing-key-that-is-also-long-enough-0123456789");

            var response = await Client(forged).PostAsJsonAsync("/agent/chat", new { message = "hi" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task Chat_is_for_students_only()
        {
            var response = await Client(TestJwt.Create(role: "Staff")).PostAsJsonAsync("/agent/chat", new { message = "hi" });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task Empty_message_is_a_bad_request(string message)
        {
            var response = await Client(TestJwt.Create()).PostAsJsonAsync("/agent/chat", new { message });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("message", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Too_long_message_is_a_bad_request()
        {
            var response = await Client(TestJwt.Create()).PostAsJsonAsync("/agent/chat", new { message = new string('x', 5000) });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Student_gets_a_reply_and_conversation_id()
        {
            factory.Model = new FakeChatClient(
                _ => FakeChatClient.CallTool("get_my_documents"),
                _ => FakeChatClient.Text("Go to **Documents** and click **Upload Document**."));

            var response = await Client(TestJwt.Create()).PostAsJsonAsync("/agent/chat", new { message = "Where do I upload my certificate?" });

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<ChatResponseBody>();
            Assert.False(string.IsNullOrWhiteSpace(body!.ConversationId));
            Assert.Contains("Upload Document", body.Reply);
            Assert.Contains(body.Actions, a => a.Route == "/student/documents");
        }

        [Fact]
        public async Task Reset_conversation_returns_no_content()
        {
            var response = await Client(TestJwt.Create()).DeleteAsync($"/agent/chat/{Guid.NewGuid():N}");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
    }

    // No LLM key: the service must still start, report it, and refuse chat with 503.
    public class UnconfiguredAgentApiFactory : WebApplicationFactory<Program>
    {
        public UnconfiguredAgentApiFactory()
        {
            // An empty env var can't override settings.json on Windows (setting "" deletes it), so pick
            // a provider and blank its key via a value that is whitespace - ResolveSettings treats it as missing.
            // This keeps the test independent of any key a developer has put in settings.json locally.
            Environment.SetEnvironmentVariable("Jwt__Key", TestJwt.Key);
            Environment.SetEnvironmentVariable("Agent__Provider", "Claude");
            Environment.SetEnvironmentVariable("Providers__Claude__ApiKey", " ");
        }

        protected override void Dispose(bool disposing)
        {
            Environment.SetEnvironmentVariable("Agent__Provider", null);
            Environment.SetEnvironmentVariable("Providers__Claude__ApiKey", null);
            base.Dispose(disposing);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Testing");
    }

    [Collection(AgentApiCollection.Name)]
    public class UnconfiguredChatEndpointTests(UnconfiguredAgentApiFactory factory) : IClassFixture<UnconfiguredAgentApiFactory>
    {
        [Fact]
        public async Task Health_reports_unconfigured()
        {
            var body = await factory.CreateClient().GetStringAsync("/agent/health");

            Assert.Contains("\"status\":\"unconfigured\"", body);
        }

        [Fact]
        public async Task Chat_returns_503_with_a_friendly_message()
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwt.Create());

            var response = await client.PostAsJsonAsync("/agent/chat", new { message = "How do I apply?" });

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("isn't set up yet", await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Unauthenticated_requests_are_still_rejected()
        {
            var response = await factory.CreateClient().PostAsJsonAsync("/agent/chat", new { message = "hi" });

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
