namespace DigitalSchoolManagementSystem.Agent.Api.Backend
{
    // Mirrors of the DigitalSchoolManagementSystem.API response DTOs - only the fields the
    // assistant needs. The backend serializes enums as numbers (no JsonStringEnumConverter),
    // so enum-typed fields are ints here; see EnumLabels for the value -> name mapping.

    public record StudentProfile
    {
        public int Id { get; init; }
        public int UserId { get; init; }
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? ProfileImageUrl { get; init; }
        public string Grade { get; init; } = string.Empty;
        public string Section { get; init; } = string.Empty;
        public int? EducationLevel { get; init; }
    }

    public record ProgramInfo
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Description { get; init; }
        public int EligibleEducationLevel { get; init; }
        public DateTime ApplicationDeadline { get; init; }
        public bool IsActive { get; init; }
        public bool IsAcceptingApplications { get; init; }
    }

    public record ProgramApplicationInfo
    {
        public int Id { get; init; }
        public int ProgramId { get; init; }
        public string ProgramName { get; init; } = string.Empty;
        public int Status { get; init; }
        public DateTime AppliedAt { get; init; }
        public DateTime? ReviewedAt { get; init; }
        public string? ReviewNotes { get; init; }
    }

    public record DocumentInfo
    {
        public int Id { get; init; }
        public int DocumentType { get; init; }
        public string? Description { get; init; }
        public DateTime UploadedAt { get; init; }
        public string FileName { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public int Status { get; init; }
        public DateTime? ReviewedAt { get; init; }
        public string? ReviewNotes { get; init; }
    }
}
