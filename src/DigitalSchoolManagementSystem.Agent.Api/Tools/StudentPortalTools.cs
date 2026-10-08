using System.ComponentModel;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using DigitalSchoolManagementSystem.Agent.Api.Knowledge;
using Microsoft.Extensions.AI;

namespace DigitalSchoolManagementSystem.Agent.Api.Tools
{
    // The assistant's tools. One instance per chat turn: every call reads the *calling student's*
    // data through IDsmsBackend (which forwards their own token), and records follow-up links in
    // Actions/ToolsCalled for the response. All tools are read-only by design - the assistant
    // guides, the student applies/uploads themselves.
    public class StudentPortalTools(IDsmsBackend backend, KnowledgeBase knowledge, TimeProvider timeProvider)
    {
        private readonly List<PortalAction> _actions = [];
        private readonly List<string> _toolsCalled = [];

        public IReadOnlyList<PortalAction> Actions => _actions;
        public IReadOnlyList<string> ToolsCalled => _toolsCalled;

        public IList<AITool> AsAITools() =>
        [
            AIFunctionFactory.Create(GetMyProfileAsync, "get_my_profile",
                "Get the student's own profile: name, grade/section, education level (needed before applying) and whether a profile photo is uploaded."),
            AIFunctionFactory.Create(ListProgramsAsync, "list_programs",
                "List all programs the student can see, with eligible education level, deadline, whether applications are open, whether this student is eligible, and whether they already applied."),
            AIFunctionFactory.Create(GetProgramDetailsAsync, "get_program_details",
                "Get one program's full details by id."),
            AIFunctionFactory.Create(GetMyApplicationsAsync, "get_my_applications",
                "List the student's program applications with status (Pending/Approved/Rejected) and staff review notes (rejection reasons)."),
            AIFunctionFactory.Create(GetMyDocumentsAsync, "get_my_documents",
                "List the documents the student has uploaded with type, status (Pending/Approved/Rejected) and staff review notes (rejection reasons)."),
            AIFunctionFactory.Create(GetRequiredDocumentsAsync, "get_required_documents",
                "Get the list of documents a student must (and may optionally) upload for a program, with notes and accepted formats. Pass programId = 0 for the general requirements for the student's education level."),
            AIFunctionFactory.Create(GetApplicationChecklistAsync, "get_application_checklist",
                "Build a personalized checklist for applying to one program: education level, eligibility, deadline, each required document's status (missing/pending/rejected/verified), and whether the student can apply now. Prefer this whenever the student asks how to apply or what they are missing."),
        ];

        public async Task<object> GetMyProfileAsync(CancellationToken cancellationToken)
        {
            Track("get_my_profile");
            return await Guard(async () =>
            {
                var profile = await RequireProfileAsync(cancellationToken);
                var hasPhoto = !string.IsNullOrWhiteSpace(profile.ProfileImageUrl);

                if (profile.EducationLevel is null || !hasPhoto)
                    AddAction(ChecklistBuilder.ProfileRoute);

                return new
                {
                    firstName = profile.FirstName,
                    lastName = profile.LastName,
                    grade = profile.Grade,
                    section = profile.Section,
                    educationLevel = EnumLabels.Label(EnumLabels.EducationLevel, profile.EducationLevel),
                    educationLevelIsSet = profile.EducationLevel is not null,
                    hasProfilePhoto = hasPhoto
                };
            });
        }

        public async Task<object> ListProgramsAsync(CancellationToken cancellationToken)
        {
            Track("list_programs");
            return await Guard(async () =>
            {
                var profile = await RequireProfileAsync(cancellationToken);
                var programs = await backend.GetProgramsAsync(cancellationToken);
                var applications = await backend.GetMyApplicationsAsync(cancellationToken);

                AddAction(ChecklistBuilder.ProgramsRoute);

                return new
                {
                    studentEducationLevel = EnumLabels.Label(EnumLabels.EducationLevel, profile.EducationLevel),
                    programs = programs.Select(p => new
                    {
                        id = p.Id,
                        name = p.Name,
                        description = p.Description,
                        eligibleEducationLevel = EnumLabels.Label(EnumLabels.EducationLevel, p.EligibleEducationLevel),
                        applicationDeadline = p.ApplicationDeadline.ToString("yyyy-MM-dd"),
                        acceptingApplications = p.IsAcceptingApplications,
                        studentIsEligible = profile.EducationLevel is null ? (bool?)null : profile.EducationLevel == p.EligibleEducationLevel,
                        alreadyApplied = applications.Any(a => a.ProgramId == p.Id),
                        pageLink = ChecklistBuilder.ProgramRoute(p.Id)
                    }).ToList()
                };
            });
        }

        public async Task<object> GetProgramDetailsAsync(
            [Description("The program id (from list_programs).")] int programId,
            CancellationToken cancellationToken)
        {
            Track("get_program_details");
            return await Guard(async () =>
            {
                var program = await backend.GetProgramAsync(programId, cancellationToken);
                if (program is null)
                    return new { error = $"No program with id {programId} exists." };

                AddAction(ChecklistBuilder.ProgramRoute(program.Id), program);
                return new
                {
                    id = program.Id,
                    name = program.Name,
                    description = program.Description,
                    eligibleEducationLevel = EnumLabels.Label(EnumLabels.EducationLevel, program.EligibleEducationLevel),
                    applicationDeadline = program.ApplicationDeadline.ToString("yyyy-MM-dd"),
                    isActive = program.IsActive,
                    acceptingApplications = program.IsAcceptingApplications,
                    pageLink = ChecklistBuilder.ProgramRoute(program.Id)
                };
            });
        }

        public async Task<object> GetMyApplicationsAsync(CancellationToken cancellationToken)
        {
            Track("get_my_applications");
            return await Guard(async () =>
            {
                var applications = await backend.GetMyApplicationsAsync(cancellationToken);
                if (applications.Count > 0)
                    AddAction(ChecklistBuilder.ProgramsRoute);

                return new
                {
                    count = applications.Count,
                    applications = applications.Select(a => new
                    {
                        programId = a.ProgramId,
                        programName = a.ProgramName,
                        status = EnumLabels.Label(EnumLabels.Status, a.Status),
                        appliedOn = a.AppliedAt.ToString("yyyy-MM-dd"),
                        reviewedOn = a.ReviewedAt?.ToString("yyyy-MM-dd"),
                        staffNotes = a.ReviewNotes
                    }).ToList()
                };
            });
        }

        public async Task<object> GetMyDocumentsAsync(CancellationToken cancellationToken)
        {
            Track("get_my_documents");
            return await Guard(async () =>
            {
                var documents = await backend.GetMyDocumentsAsync(cancellationToken);
                AddAction(ChecklistBuilder.DocumentsRoute);

                return new
                {
                    count = documents.Count,
                    documents = documents.Select(d => new
                    {
                        type = EnumLabels.Label(EnumLabels.DocumentType, d.DocumentType),
                        fileName = d.FileName,
                        description = d.Description,
                        uploadedOn = d.UploadedAt.ToString("yyyy-MM-dd"),
                        status = EnumLabels.Label(EnumLabels.Status, d.Status),
                        staffNotes = d.ReviewNotes
                    }).ToList()
                };
            });
        }

        public async Task<object> GetRequiredDocumentsAsync(
            [Description("Program id from list_programs, or 0 for general requirements based on the student's education level.")] int programId,
            CancellationToken cancellationToken)
        {
            Track("get_required_documents");
            return await Guard(async () =>
            {
                int? level;
                string? programName = null;

                if (programId > 0)
                {
                    var program = await backend.GetProgramAsync(programId, cancellationToken);
                    if (program is null)
                        return new { error = $"No program with id {programId} exists." };
                    level = program.EligibleEducationLevel;
                    programName = program.Name;
                }
                else
                {
                    level = (await RequireProfileAsync(cancellationToken)).EducationLevel;
                }

                AddAction(ChecklistBuilder.DocumentsRoute);
                return new
                {
                    program = programName,
                    basedOnEducationLevel = EnumLabels.Label(EnumLabels.EducationLevel, level),
                    documents = knowledge.GetRequirements(level, programName).Select(r => new
                    {
                        name = r.Label,
                        uploadAs = EnumLabels.Label(EnumLabels.DocumentType, r.DocumentType),
                        uploadFrom = r.DocumentType == EnumLabels.DocumentTypes.ProfilePhoto ? "My Profile (camera button on avatar)" : "Documents page > Upload Document",
                        required = r.Required,
                        notes = r.Notes,
                        acceptedFormats = r.AcceptedFormats
                    }).ToList()
                };
            });
        }

        public async Task<object> GetApplicationChecklistAsync(
            [Description("The program id (from list_programs).")] int programId,
            CancellationToken cancellationToken)
        {
            Track("get_application_checklist");
            return await Guard(async () =>
            {
                var program = await backend.GetProgramAsync(programId, cancellationToken);
                if (program is null)
                    return new { error = $"No program with id {programId} exists." };

                var profile = await RequireProfileAsync(cancellationToken);
                var applications = await backend.GetMyApplicationsAsync(cancellationToken);
                var documents = await backend.GetMyDocumentsAsync(cancellationToken);
                var requirements = knowledge.GetRequirements(program.EligibleEducationLevel, program.Name);

                var checklist = ChecklistBuilder.Build(profile, program, applications, documents, requirements, timeProvider.GetUtcNow().UtcDateTime);
                foreach (var action in checklist.Actions)
                    AddAction(action.Route, program);

                return checklist;
            });
        }

        private async Task<StudentProfile> RequireProfileAsync(CancellationToken cancellationToken) =>
            await backend.GetMyProfileAsync(cancellationToken)
                ?? throw new BackendException("The student's profile could not be found.");

        // Backend failures become a tool result the model can explain, instead of failing the whole turn.
        private static async Task<object> Guard(Func<Task<object>> body)
        {
            try
            {
                return await body();
            }
            catch (BackendException ex)
            {
                return new { error = ex.Message, advice = "Tell the student the portal data is temporarily unavailable and to try again shortly." };
            }
        }

        private void Track(string toolName) => _toolsCalled.Add(toolName);

        private void AddAction(string route, ProgramInfo? program = null)
        {
            if (_actions.Any(a => a.Route == route))
                return;
            _actions.Add(new PortalAction(ChecklistBuilder.LabelFor(route, program), route));
        }
    }
}
