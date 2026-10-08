namespace DigitalSchoolManagementSystem.Agent.Api.Agent
{
    // Whether an LLM provider is usable. Without an API key the service still starts (so a
    // container stack comes up cleanly), reports "unconfigured" on /agent/health and answers
    // chat requests with 503 until a key is supplied and the service restarted.
    public record AssistantStatus(string? ConfigurationProblem)
    {
        public bool IsConfigured => ConfigurationProblem is null;
    }
}
