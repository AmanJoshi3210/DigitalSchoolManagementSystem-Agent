namespace DigitalSchoolManagementSystem.Agent.Api.Backend
{
    // Numeric values match DigitalSchoolManagementSystem.Domain enums exactly
    // (declaration order / explicit assignments) - keep in sync if the backend changes them.
    public static class EnumLabels
    {
        public static class DocumentTypes
        {
            public const int Passport = 1;
            public const int OfferLetter = 2;
            public const int Certificate = 3;
            public const int ProfilePhoto = 4;
            public const int Other = 5;
        }

        public static class ReviewStatus
        {
            public const int Pending = 0;
            public const int Approved = 1;
            public const int Rejected = 2;
        }

        public static readonly IReadOnlyDictionary<int, string> DocumentType = new Dictionary<int, string>
        {
            [DocumentTypes.Passport] = "Passport",
            [DocumentTypes.OfferLetter] = "Offer Letter",
            [DocumentTypes.Certificate] = "Certificate",
            [DocumentTypes.ProfilePhoto] = "Profile Photo",
            [DocumentTypes.Other] = "Other",
        };

        public static readonly IReadOnlyDictionary<int, string> EducationLevel = new Dictionary<int, string>
        {
            [0] = "Undergraduate",
            [1] = "Graduate",
            [2] = "Post Graduate",
        };

        // ApplicationStatus and DocumentStatus share the same values.
        public static readonly IReadOnlyDictionary<int, string> Status = new Dictionary<int, string>
        {
            [ReviewStatus.Pending] = "Pending",
            [ReviewStatus.Approved] = "Approved",
            [ReviewStatus.Rejected] = "Rejected",
        };

        public static string Label(IReadOnlyDictionary<int, string> labels, int? value) =>
            value is null ? "Not set" : labels.TryGetValue(value.Value, out var label) ? label : $"Unknown ({value})";

        // Accepts "Passport", "offer letter", "OfferLetter", "ProfilePhoto"... (used by document-requirements.json).
        public static int? ParseDocumentType(string name)
        {
            var normalized = name.Replace(" ", string.Empty);
            foreach (var (value, label) in DocumentType)
            {
                if (string.Equals(label.Replace(" ", string.Empty), normalized, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
            return null;
        }

        public static int? ParseEducationLevel(string name)
        {
            var normalized = name.Replace(" ", string.Empty);
            foreach (var (value, label) in EducationLevel)
            {
                if (string.Equals(label.Replace(" ", string.Empty), normalized, StringComparison.OrdinalIgnoreCase))
                    return value;
            }
            return null;
        }
    }
}
