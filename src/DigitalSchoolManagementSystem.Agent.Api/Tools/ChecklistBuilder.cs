using System.Text.Json.Serialization;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using DigitalSchoolManagementSystem.Agent.Api.Knowledge;

namespace DigitalSchoolManagementSystem.Agent.Api.Tools
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChecklistState
    {
        Done,
        Pending,
        Missing,
        Rejected,
        Blocked,
        Optional
    }

    public record ChecklistItem(string Title, ChecklistState State, string Detail, string? Route);

    public record PortalAction(string Label, string Route);

    public record ApplicationChecklist(
        int ProgramId,
        string ProgramName,
        // The backend will accept an Apply right now (level set + eligible + open + not applied).
        bool CanApplyNow,
        // CanApplyNow AND every required document is uploaded (pending or approved).
        bool ReadyToApply,
        string? ExistingApplicationStatus,
        IReadOnlyList<ChecklistItem> Items,
        IReadOnlyList<PortalAction> Actions);

    // Deterministic "what is this student still missing for program X" logic. Kept out of the
    // LLM on purpose: the model explains the checklist, it doesn't compute it.
    public static class ChecklistBuilder
    {
        public const string ProfileRoute = "/student/profile";
        public const string DocumentsRoute = "/student/documents";
        public const string ProgramsRoute = "/student/programs";
        public const string MessagesRoute = "/student/messages";

        public static string ProgramRoute(int programId) => $"{ProgramsRoute}/{programId}";

        public static ApplicationChecklist Build(
            StudentProfile profile,
            ProgramInfo program,
            IReadOnlyList<ProgramApplicationInfo> applications,
            IReadOnlyList<DocumentInfo> documents,
            IReadOnlyList<DocumentRequirement> requirements,
            DateTime todayUtc)
        {
            var items = new List<ChecklistItem>();

            var existing = applications.FirstOrDefault(a => a.ProgramId == program.Id);
            if (existing is not null)
            {
                var status = EnumLabels.Label(EnumLabels.Status, existing.Status);
                var detail = $"You applied on {existing.AppliedAt:dd MMM yyyy}. Current status: {status}.";
                if (existing.Status == EnumLabels.ReviewStatus.Rejected && !string.IsNullOrWhiteSpace(existing.ReviewNotes))
                    detail += $" Reason given by staff: {existing.ReviewNotes}";
                items.Add(new ChecklistItem("Application submitted", ChecklistState.Done, detail, ProgramRoute(program.Id)));
            }

            var levelSet = profile.EducationLevel is not null;
            items.Add(levelSet
                ? new ChecklistItem("Education level set", ChecklistState.Done,
                    $"Your education level is {EnumLabels.Label(EnumLabels.EducationLevel, profile.EducationLevel)}.", ProfileRoute)
                : new ChecklistItem("Education level set", ChecklistState.Missing,
                    "Set your Education Level in My Profile (Edit Profile) - applying is blocked until you do.", ProfileRoute));

            var programLevel = EnumLabels.Label(EnumLabels.EducationLevel, program.EligibleEducationLevel);
            var eligible = levelSet && profile.EducationLevel == program.EligibleEducationLevel;
            if (levelSet)
            {
                items.Add(eligible
                    ? new ChecklistItem("Eligible for this program", ChecklistState.Done, $"This program is for {programLevel} students.", null)
                    : new ChecklistItem("Eligible for this program", ChecklistState.Blocked,
                        $"This program is only for {programLevel} students, and your education level is {EnumLabels.Label(EnumLabels.EducationLevel, profile.EducationLevel)}.", ProgramsRoute));
            }

            var deadline = program.ApplicationDeadline.Date;
            if (program.IsAcceptingApplications)
            {
                var daysLeft = (deadline - todayUtc.Date).Days;
                var when = daysLeft == 0 ? "today" : $"in {daysLeft} day{(daysLeft == 1 ? "" : "s")}";
                items.Add(new ChecklistItem("Applications open", ChecklistState.Done,
                    $"Deadline is {deadline:dd MMM yyyy} ({when}).", ProgramRoute(program.Id)));
            }
            else
            {
                var reason = !program.IsActive
                    ? "This program is not currently active."
                    : $"The application deadline ({deadline:dd MMM yyyy}) has passed.";
                items.Add(new ChecklistItem("Applications open", ChecklistState.Blocked, reason, ProgramsRoute));
            }

            var allRequiredUploaded = true;
            foreach (var requirement in requirements)
            {
                var item = BuildDocumentItem(requirement, profile, documents);
                if (requirement.Required && item.State is ChecklistState.Missing or ChecklistState.Rejected)
                    allRequiredUploaded = false;
                items.Add(item);
            }

            var canApplyNow = existing is null && eligible && program.IsAcceptingApplications;

            var actions = items
                .Where(i => i.Route is not null && i.State is not (ChecklistState.Done or ChecklistState.Optional))
                .Select(i => i.Route!)
                .Append(canApplyNow ? ProgramRoute(program.Id) : null)
                .OfType<string>()
                .Distinct()
                .Select(route => new PortalAction(LabelFor(route, program), route))
                .ToList();

            return new ApplicationChecklist(
                program.Id,
                program.Name,
                canApplyNow,
                canApplyNow && allRequiredUploaded,
                existing is null ? null : EnumLabels.Label(EnumLabels.Status, existing.Status),
                items,
                actions);
        }

        private static ChecklistItem BuildDocumentItem(DocumentRequirement requirement, StudentProfile profile, IReadOnlyList<DocumentInfo> documents)
        {
            var title = requirement.Label;
            var formats = requirement.AcceptedFormats.Count > 0 ? $" Accepted formats: {string.Join(", ", requirement.AcceptedFormats)}." : "";
            var notes = string.IsNullOrWhiteSpace(requirement.Notes) ? "" : $" {requirement.Notes}";

            // Profile photos are uploaded from My Profile and auto-approved; the profile URL is the source of truth.
            if (requirement.DocumentType == EnumLabels.DocumentTypes.ProfilePhoto)
            {
                var hasPhoto = !string.IsNullOrWhiteSpace(profile.ProfileImageUrl)
                    || documents.Any(d => d.DocumentType == EnumLabels.DocumentTypes.ProfilePhoto);
                if (hasPhoto)
                    return new ChecklistItem(title, ChecklistState.Done, "Profile photo uploaded.", ProfileRoute);
                return new ChecklistItem(title,
                    requirement.Required ? ChecklistState.Missing : ChecklistState.Optional,
                    $"Upload it from My Profile using the camera button on your avatar.{notes}{formats}",
                    ProfileRoute);
            }

            var ofType = documents.Where(d => d.DocumentType == requirement.DocumentType).ToList();
            var typeName = EnumLabels.Label(EnumLabels.DocumentType, requirement.DocumentType);

            if (ofType.Any(d => d.Status == EnumLabels.ReviewStatus.Approved))
                return new ChecklistItem(title, ChecklistState.Done, $"Uploaded and verified by staff ({typeName}).", DocumentsRoute);

            var pending = ofType.FirstOrDefault(d => d.Status == EnumLabels.ReviewStatus.Pending);
            if (pending is not null)
                return new ChecklistItem(title, ChecklistState.Pending,
                    $"'{pending.FileName}' is uploaded and waiting for staff review. Nothing more to do unless it gets rejected.", DocumentsRoute);

            var rejected = ofType.FirstOrDefault(d => d.Status == EnumLabels.ReviewStatus.Rejected);
            if (rejected is not null)
            {
                var reason = string.IsNullOrWhiteSpace(rejected.ReviewNotes) ? "No reason was given." : $"Reason: {rejected.ReviewNotes}";
                return new ChecklistItem(title, ChecklistState.Rejected,
                    $"'{rejected.FileName}' was rejected. {reason} Upload a corrected file as a new '{typeName}' document.{formats}", DocumentsRoute);
            }

            return new ChecklistItem(title,
                requirement.Required ? ChecklistState.Missing : ChecklistState.Optional,
                $"Upload it on the Documents page with Document Type '{typeName}'.{notes}{formats}",
                DocumentsRoute);
        }

        public static string LabelFor(string route, ProgramInfo? program = null) => route switch
        {
            ProfileRoute => "Open My Profile",
            DocumentsRoute => "Open Documents",
            ProgramsRoute => "Browse Programs",
            MessagesRoute => "Message Staff",
            _ when program is not null && route == ProgramRoute(program.Id) => $"Open {program.Name}",
            _ => "Open page"
        };
    }
}
