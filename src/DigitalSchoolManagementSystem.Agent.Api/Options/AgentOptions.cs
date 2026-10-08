namespace DigitalSchoolManagementSystem.Agent.Api.Options
{
    // Which LLM backs the assistant. Switch it with "Agent:Provider" in settings.json
    // (or the Agent__Provider environment variable) - no code change needed.
    public enum LlmProvider
    {
        Gemini,
        OpenAI,
        Claude
    }

    public class AgentOptions
    {
        public const string SectionName = "Agent";

        public LlmProvider Provider { get; set; } = LlmProvider.Gemini;
        public float Temperature { get; set; } = 0.3f;
        public int MaxOutputTokens { get; set; } = 1024;
        public int MaxToolIterations { get; set; } = 6;
        public int MaxHistoryMessages { get; set; } = 20;
        public int MaxUserMessageChars { get; set; } = 2000;
        public int RequestTimeoutSeconds { get; set; } = 30;
        public int ConversationIdleMinutes { get; set; } = 30;
        public int RequestsPerMinutePerUser { get; set; } = 20;
    }

    public class ProviderSettings
    {
        public string Model { get; set; } = string.Empty;

        // Never commit a real key to settings.json - supply it via environment variables
        // (Providers__Gemini__ApiKey, ...) or `dotnet user-secrets`.
        public string ApiKey { get; set; } = string.Empty;
    }

    public class ProvidersOptions
    {
        public const string SectionName = "Providers";

        public ProviderSettings Gemini { get; set; } = new();
        public ProviderSettings OpenAI { get; set; } = new();
        public ProviderSettings Claude { get; set; } = new();

        public ProviderSettings For(LlmProvider provider) => provider switch
        {
            LlmProvider.Gemini => Gemini,
            LlmProvider.OpenAI => OpenAI,
            LlmProvider.Claude => Claude,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown LLM provider.")
        };
    }

    public class BackendOptions
    {
        public const string SectionName = "Backend";

        // DigitalSchoolManagementSystem.API base URL, including the /api segment.
        public string BaseUrl { get; set; } = "http://localhost:5081/api";
    }

    public class JwtOptions
    {
        public const string SectionName = "Jwt";

        public string Issuer { get; set; } = string.Empty;
        public string Audience { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
    }
}
