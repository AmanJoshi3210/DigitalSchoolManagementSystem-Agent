using DigitalSchoolManagementSystem.Agent.Api.Llm;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using Microsoft.Extensions.AI;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    public class LlmProviderFactoryTests
    {
        private static ProvidersOptions Providers(string key = "test-key") => new()
        {
            Gemini = new ProviderSettings { Model = "gemini-test", ApiKey = key },
            OpenAI = new ProviderSettings { Model = "gpt-test", ApiKey = key },
            Claude = new ProviderSettings { Model = "claude-test", ApiKey = key },
        };

        [Theory]
        [InlineData(LlmProvider.Gemini, "gemini-test")]
        [InlineData(LlmProvider.OpenAI, "gpt-test")]
        [InlineData(LlmProvider.Claude, "claude-test")]
        public void Creates_a_client_for_the_configured_provider_and_model(LlmProvider provider, string expectedModel)
        {
            using var client = LlmProviderFactory.CreateInnerClient(new AgentOptions { Provider = provider }, Providers());

            var metadata = client.GetService<ChatClientMetadata>();
            Assert.NotNull(metadata);
            Assert.Equal(expectedModel, metadata.DefaultModelId);
        }

        [Fact]
        public void Each_provider_maps_to_a_different_vendor_adapter()
        {
            var types = Enum.GetValues<LlmProvider>()
                .Select(p =>
                {
                    using var client = LlmProviderFactory.CreateInnerClient(new AgentOptions { Provider = p }, Providers());
                    return client.GetType().FullName;
                })
                .ToList();

            Assert.Equal(types.Count, types.Distinct().Count());
        }

        [Theory]
        [InlineData(LlmProvider.Gemini)]
        [InlineData(LlmProvider.OpenAI)]
        [InlineData(LlmProvider.Claude)]
        public void Missing_api_key_fails_with_a_helpful_message(LlmProvider provider)
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                LlmProviderFactory.CreateInnerClient(new AgentOptions { Provider = provider }, Providers(key: "")));

            Assert.Contains($"Providers__{provider}__ApiKey", ex.Message);
        }

        [Fact]
        public void Only_the_selected_provider_needs_a_key()
        {
            var providers = Providers(key: "");
            providers.Gemini.ApiKey = "only-gemini";

            var settings = LlmProviderFactory.ResolveSettings(new AgentOptions { Provider = LlmProvider.Gemini }, providers);

            Assert.Equal("gemini-test", settings.Model);
        }

        [Fact]
        public void Pipeline_adds_function_invocation_with_configured_iteration_cap()
        {
            using var pipeline = LlmProviderFactory.WithAgentPipeline(new FakeChatClient(), new AgentOptions { MaxToolIterations = 3 });

            var invoker = pipeline.GetService<FunctionInvokingChatClient>();
            Assert.NotNull(invoker);
            Assert.Equal(3, invoker.MaximumIterationsPerRequest);
        }
    }
}
