using DigitalSchoolManagementSystem.Agent.Api.Backend;
using DigitalSchoolManagementSystem.Agent.Api.Knowledge;
using DigitalSchoolManagementSystem.Agent.Api.Tools;
using static DigitalSchoolManagementSystem.Agent.Api.Backend.EnumLabels;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    public class ChecklistBuilderTests
    {
        private static readonly IReadOnlyList<DocumentRequirement> Requirements =
        [
            new(DocumentTypes.ProfilePhoto, "Profile photo", true, null, ["JPEG"]),
            new(DocumentTypes.Certificate, "Class 12 certificate", true, null, ["PDF"]),
            new(DocumentTypes.Passport, "Passport", false, null, []),
        ];

        private static ApplicationChecklist Build(
            StudentProfile? profile = null,
            ProgramInfo? program = null,
            List<ProgramApplicationInfo>? applications = null,
            List<DocumentInfo>? documents = null) =>
            ChecklistBuilder.Build(
                profile ?? TestData.Profile(),
                program ?? TestData.Program(),
                applications ?? [],
                documents ?? [],
                Requirements,
                TestData.Today);

        private static ChecklistItem Item(ApplicationChecklist checklist, string title) =>
            checklist.Items.Single(i => i.Title == title);

        [Fact]
        public void Missing_education_level_blocks_applying_and_links_profile()
        {
            var checklist = Build(profile: TestData.Profile(educationLevel: null));

            Assert.False(checklist.CanApplyNow);
            Assert.Equal(ChecklistState.Missing, Item(checklist, "Education level set").State);
            Assert.DoesNotContain(checklist.Items, i => i.Title == "Eligible for this program");
            Assert.Contains(checklist.Actions, a => a.Route == ChecklistBuilder.ProfileRoute);
        }

        [Fact]
        public void Wrong_education_level_is_blocked_with_explanation()
        {
            var checklist = Build(profile: TestData.Profile(educationLevel: TestData.Graduate));

            var eligibility = Item(checklist, "Eligible for this program");
            Assert.Equal(ChecklistState.Blocked, eligibility.State);
            Assert.Contains("only for Undergraduate", eligibility.Detail);
            Assert.False(checklist.CanApplyNow);
        }

        [Fact]
        public void Deadline_passed_is_blocked()
        {
            var checklist = Build(program: TestData.Program(deadlineInDays: -1));

            var open = Item(checklist, "Applications open");
            Assert.Equal(ChecklistState.Blocked, open.State);
            Assert.Contains("has passed", open.Detail);
            Assert.False(checklist.CanApplyNow);
        }

        [Fact]
        public void Inactive_program_says_not_active()
        {
            var checklist = Build(program: TestData.Program(active: false));

            Assert.Contains("not currently active", Item(checklist, "Applications open").Detail);
        }

        [Fact]
        public void Eligible_student_without_documents_can_apply_but_is_not_ready()
        {
            var checklist = Build();

            Assert.True(checklist.CanApplyNow);
            Assert.False(checklist.ReadyToApply);
            Assert.Equal(ChecklistState.Missing, Item(checklist, "Profile photo").State);
            Assert.Equal(ChecklistState.Missing, Item(checklist, "Class 12 certificate").State);
            Assert.Equal(ChecklistState.Optional, Item(checklist, "Passport").State);
            Assert.Contains(checklist.Actions, a => a.Route == ChecklistBuilder.DocumentsRoute);
            Assert.Contains(checklist.Actions, a => a.Route == ChecklistBuilder.ProgramRoute(1));
            Assert.Contains("in 10 days", Item(checklist, "Applications open").Detail);
        }

        [Fact]
        public void Pending_documents_count_as_uploaded()
        {
            var checklist = Build(
                profile: TestData.Profile(photoUrl: "https://cdn/photo.jpg"),
                documents: [TestData.Document(DocumentTypes.Certificate, ReviewStatus.Pending, "marks.pdf")]);

            Assert.Equal(ChecklistState.Done, Item(checklist, "Profile photo").State);
            var certificate = Item(checklist, "Class 12 certificate");
            Assert.Equal(ChecklistState.Pending, certificate.State);
            Assert.Contains("marks.pdf", certificate.Detail);
            Assert.True(checklist.ReadyToApply);
        }

        [Fact]
        public void Rejected_document_shows_reason_and_is_not_ready()
        {
            var checklist = Build(
                profile: TestData.Profile(photoUrl: "https://cdn/photo.jpg"),
                documents: [TestData.Document(DocumentTypes.Certificate, ReviewStatus.Rejected, "blurry.jpg", "Scan is unreadable")]);

            var certificate = Item(checklist, "Class 12 certificate");
            Assert.Equal(ChecklistState.Rejected, certificate.State);
            Assert.Contains("Scan is unreadable", certificate.Detail);
            Assert.False(checklist.ReadyToApply);
        }

        [Fact]
        public void Approved_document_wins_over_an_older_rejected_one()
        {
            var checklist = Build(
                profile: TestData.Profile(photoUrl: "https://cdn/photo.jpg"),
                documents:
                [
                    TestData.Document(DocumentTypes.Certificate, ReviewStatus.Rejected),
                    TestData.Document(DocumentTypes.Certificate, ReviewStatus.Approved)
                ]);

            Assert.Equal(ChecklistState.Done, Item(checklist, "Class 12 certificate").State);
            Assert.True(checklist.ReadyToApply);
        }

        [Fact]
        public void Profile_photo_document_counts_even_without_profile_url()
        {
            var checklist = Build(documents: [TestData.Document(DocumentTypes.ProfilePhoto, ReviewStatus.Approved)]);

            Assert.Equal(ChecklistState.Done, Item(checklist, "Profile photo").State);
        }

        [Fact]
        public void Already_applied_cannot_apply_again_and_reports_status_and_reason()
        {
            var checklist = Build(applications: [TestData.Application(1, ReviewStatus.Rejected, "Seats are full")]);

            Assert.False(checklist.CanApplyNow);
            Assert.Equal("Rejected", checklist.ExistingApplicationStatus);
            Assert.Contains("Seats are full", Item(checklist, "Application submitted").Detail);
        }

        [Fact]
        public void Application_to_a_different_program_does_not_count()
        {
            var checklist = Build(applications: [TestData.Application(programId: 2, ReviewStatus.Pending)]);

            Assert.True(checklist.CanApplyNow);
            Assert.Null(checklist.ExistingApplicationStatus);
        }
    }
}
