using DigitalSchoolManagementSystem.Agent.Api.Knowledge;
using static DigitalSchoolManagementSystem.Agent.Api.Backend.EnumLabels;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    public class KnowledgeBaseTests
    {
        private const string Json = """
            {
              "default": [
                { "documentType": "ProfilePhoto", "label": "Photo", "required": true },
                { "documentType": "Passport", "label": "Passport", "required": false }
              ],
              "byEducationLevel": {
                "Graduate": [ { "documentType": "Certificate", "label": "Bachelor's degree", "required": true } ]
              },
              "byProgram": {
                "Exchange Program": [ { "documentType": "Passport", "label": "Passport (valid 6 months)", "required": true },
                                      { "documentType": "Offer Letter", "label": "Offer letter", "required": true } ]
              }
            }
            """;

        [Fact]
        public void Default_rules_apply_when_nothing_matches()
        {
            var kb = new KnowledgeBase("guide", Json);

            var rules = kb.GetRequirements(eligibleEducationLevel: 0, programName: "Unknown program");

            Assert.Equal([DocumentTypes.ProfilePhoto, DocumentTypes.Passport], rules.Select(r => r.DocumentType));
        }

        [Fact]
        public void Education_level_rules_are_added()
        {
            var kb = new KnowledgeBase("guide", Json);

            var rules = kb.GetRequirements(eligibleEducationLevel: 1, programName: null);

            Assert.Contains(rules, r => r.DocumentType == DocumentTypes.Certificate && r.Label == "Bachelor's degree");
        }

        [Fact]
        public void Program_rules_override_defaults_case_insensitively()
        {
            var kb = new KnowledgeBase("guide", Json);

            var rules = kb.GetRequirements(eligibleEducationLevel: 0, programName: "  exchange PROGRAM ");

            var passport = Assert.Single(rules, r => r.DocumentType == DocumentTypes.Passport);
            Assert.True(passport.Required);
            Assert.Equal("Passport (valid 6 months)", passport.Label);
            Assert.Contains(rules, r => r.DocumentType == DocumentTypes.OfferLetter);
            Assert.True(rules.TakeWhile(r => r.Required).Count() == rules.Count(r => r.Required), "Required documents are listed first");
        }

        [Fact]
        public void Unknown_document_type_fails_loudly()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                new KnowledgeBase("guide", """{ "default": [ { "documentType": "Visa", "required": true } ] }"""));

            Assert.Contains("Visa", ex.Message);
        }

        [Fact]
        public void Unknown_education_level_fails_loudly()
        {
            Assert.Throws<InvalidOperationException>(() =>
                new KnowledgeBase("guide", """{ "byEducationLevel": { "PhD": [] } }"""));
        }

        [Fact]
        public void Shipped_knowledge_files_load()
        {
            var kb = KnowledgeBase.LoadFrom(Path.Combine(AppContext.BaseDirectory, "Knowledge"));

            Assert.Contains("Upload Document", kb.PortalGuide);
            Assert.Contains(kb.GetRequirements(0, null), r => r.DocumentType == DocumentTypes.ProfilePhoto && r.Required);
        }
    }
}
