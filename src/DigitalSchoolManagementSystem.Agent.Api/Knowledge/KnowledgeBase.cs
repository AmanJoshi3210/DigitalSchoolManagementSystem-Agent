using System.Text.Json;
using DigitalSchoolManagementSystem.Agent.Api.Backend;

namespace DigitalSchoolManagementSystem.Agent.Api.Knowledge
{
    public record DocumentRequirement(
        int DocumentType,
        string Label,
        bool Required,
        string? Notes,
        IReadOnlyList<string> AcceptedFormats);

    // Static knowledge the assistant is grounded in: the portal guide (embedded whole in the
    // system prompt) and the editable document-requirements.json rules.
    public class KnowledgeBase
    {
        public const string GuideFileName = "portal-guide.md";
        public const string RequirementsFileName = "document-requirements.json";

        private readonly IReadOnlyList<DocumentRequirement> _default;
        private readonly IReadOnlyDictionary<int, IReadOnlyList<DocumentRequirement>> _byEducationLevel;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<DocumentRequirement>> _byProgram;

        public string PortalGuide { get; }

        public KnowledgeBase(string portalGuide, string requirementsJson)
        {
            PortalGuide = portalGuide;

            var file = JsonSerializer.Deserialize<RequirementsFile>(requirementsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidOperationException($"{RequirementsFileName} is empty.");

            _default = Convert(file.Default, "default");

            _byEducationLevel = (file.ByEducationLevel ?? []).ToDictionary(
                kv => EnumLabels.ParseEducationLevel(kv.Key)
                    ?? throw new InvalidOperationException($"{RequirementsFileName}: unknown education level '{kv.Key}' in byEducationLevel."),
                kv => Convert(kv.Value, $"byEducationLevel.{kv.Key}"));

            _byProgram = (file.ByProgram ?? []).ToDictionary(
                kv => kv.Key.Trim(),
                kv => Convert(kv.Value, $"byProgram.{kv.Key}"),
                StringComparer.OrdinalIgnoreCase);
        }

        public static KnowledgeBase LoadFrom(string directory) => new(
            File.ReadAllText(Path.Combine(directory, GuideFileName)),
            File.ReadAllText(Path.Combine(directory, RequirementsFileName)));

        // Merges default -> education level -> program; a later rule for the same document type wins.
        public IReadOnlyList<DocumentRequirement> GetRequirements(int? eligibleEducationLevel, string? programName)
        {
            var merged = new Dictionary<int, DocumentRequirement>();

            void Apply(IEnumerable<DocumentRequirement> rules)
            {
                foreach (var rule in rules)
                    merged[rule.DocumentType] = rule;
            }

            Apply(_default);
            if (eligibleEducationLevel is int level && _byEducationLevel.TryGetValue(level, out var levelRules))
                Apply(levelRules);
            if (!string.IsNullOrWhiteSpace(programName) && _byProgram.TryGetValue(programName.Trim(), out var programRules))
                Apply(programRules);

            return merged.Values.OrderByDescending(r => r.Required).ThenBy(r => r.DocumentType).ToList();
        }

        private static IReadOnlyList<DocumentRequirement> Convert(List<RequirementEntry>? entries, string location) =>
            (entries ?? []).Select(e => new DocumentRequirement(
                EnumLabels.ParseDocumentType(e.DocumentType ?? string.Empty)
                    ?? throw new InvalidOperationException($"{RequirementsFileName}: unknown documentType '{e.DocumentType}' in {location}."),
                string.IsNullOrWhiteSpace(e.Label) ? e.DocumentType! : e.Label,
                e.Required,
                e.Notes,
                e.AcceptedFormats ?? [])).ToList();

        private sealed class RequirementsFile
        {
            public List<RequirementEntry>? Default { get; set; }
            public Dictionary<string, List<RequirementEntry>>? ByEducationLevel { get; set; }
            public Dictionary<string, List<RequirementEntry>>? ByProgram { get; set; }
        }

        private sealed class RequirementEntry
        {
            public string? DocumentType { get; set; }
            public string? Label { get; set; }
            public bool Required { get; set; }
            public string? Notes { get; set; }
            public List<string>? AcceptedFormats { get; set; }
        }
    }
}
