using Anthropic;
using DigitalSchoolManagementSystem.Agent.Api.Options;
using Microsoft.Extensions.AI;

namespace DigitalSchoolManagementSystem.Agent.Api.Llm
{
    // The only place that knows about individual LLM vendors. Everything else talks to the
    // provider-neutral Microsoft.Extensions.AI IChatClient, so switching "Agent:Provider" in
    // settings.json is the whole migration.
    public static class LlmProviderFactory
    {
        public static ProviderSettings ResolveSettings(AgentOptions agent, ProvidersOptions providers)
        {
            var settings = providers.For(agent.Provider);

            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                throw new InvalidOperationException(
                    $"Agent:Provider is '{agent.Provider}' but Providers:{agent.Provider}:ApiKey is empty. " +
                    $"Set the environment variable Providers__{agent.Provider}__ApiKey (or use `dotnet user-secrets set \"Providers:{agent.Provider}:ApiKey\" <key>`).");
            }

            if (string.IsNullOrWhiteSpace(settings.Model))
                throw new InvalidOperationException($"Providers:{agent.Provider}:Model is empty in settings.json.");

            return settings;
        }

        // The raw vendor client, without function invocation.
        public static IChatClient CreateInnerClient(AgentOptions agent, ProvidersOptions providers)
        {
            var settings = ResolveSettings(agent, providers);

            return agent.Provider switch
            {
                LlmProvider.Gemini => new Google.GenAI.Client(apiKey: settings.ApiKey).AsIChatClient(settings.Model),
                LlmProvider.OpenAI => new OpenAI.Chat.ChatClient(settings.Model, settings.ApiKey).AsIChatClient(),
                LlmProvider.Claude => new AnthropicClient { ApiKey = settings.ApiKey }.AsIChatClient(settings.Model, agent.MaxOutputTokens),
                _ => throw new InvalidOperationException($"Unsupported LLM provider '{agent.Provider}'.")
            };
        }

        // Wraps any IChatClient with the tool-calling loop. Separate from CreateInnerClient so tests
        // can run the real loop over a fake model.
        public static IChatClient WithAgentPipeline(IChatClient inner, AgentOptions agent, ILoggerFactory? loggerFactory = null) =>
            new ChatClientBuilder(inner)
                .UseFunctionInvocation(loggerFactory, client =>
                {
                    client.MaximumIterationsPerRequest = agent.MaxToolIterations;
                    client.MaximumConsecutiveErrorsPerRequest = 2;
                    client.IncludeDetailedErrors = false;
                })
                .Build();
    }
}
