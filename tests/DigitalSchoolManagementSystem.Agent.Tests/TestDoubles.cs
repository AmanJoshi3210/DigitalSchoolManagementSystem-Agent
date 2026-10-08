using System.IdentityModel.Tokens.Jwt;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using Microsoft.Extensions.AI;
using Microsoft.IdentityModel.Tokens;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    // Scripted LLM: each call pops the next response; records every prompt it was sent.
    public class FakeChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script) : IChatClient
    {
        private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _script = new(script);

        public List<List<ChatMessage>> Calls { get; } = [];
        public List<ChatOptions?> Options { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Calls.Add(list);
            Options.Add(options);
            var next = _script.Count > 0 ? _script.Dequeue() : _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "(no more scripted responses)"));
            return Task.FromResult(next(list));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
                yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }

        public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

        public static ChatResponse CallTool(string name, Dictionary<string, object?>? arguments = null) =>
            new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(Guid.NewGuid().ToString("N"), name, arguments)]));
    }

    public class FakeBackend : IDsmsBackend
    {
        public StudentProfile? Profile { get; set; } = TestData.Profile();
        public List<ProgramInfo> Programs { get; set; } = [TestData.Program()];
        public List<ProgramApplicationInfo> Applications { get; set; } = [];
        public List<DocumentInfo> Documents { get; set; } = [];
        public bool Fail { get; set; }

        public Task<StudentProfile?> GetMyProfileAsync(CancellationToken cancellationToken = default) => Run(() => Profile);
        public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken cancellationToken = default) => Run<IReadOnlyList<ProgramInfo>>(() => Programs);
        public Task<ProgramInfo?> GetProgramAsync(int programId, CancellationToken cancellationToken = default) => Run(() => Programs.FirstOrDefault(p => p.Id == programId));
        public Task<IReadOnlyList<ProgramApplicationInfo>> GetMyApplicationsAsync(CancellationToken cancellationToken = default) => Run<IReadOnlyList<ProgramApplicationInfo>>(() => Applications);
        public Task<IReadOnlyList<DocumentInfo>> GetMyDocumentsAsync(CancellationToken cancellationToken = default) => Run<IReadOnlyList<DocumentInfo>>(() => Documents);

        private Task<T> Run<T>(Func<T> value) =>
            Fail ? Task.FromException<T>(new BackendException("The school portal API returned 500.")) : Task.FromResult(value());
    }

    public static class TestData
    {
        public static readonly DateTime Today = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

        public const int Undergraduate = 0;
        public const int Graduate = 1;

        public static StudentProfile Profile(int? educationLevel = Undergraduate, string? photoUrl = null) => new()
        {
            Id = 7,
            UserId = 42,
            FirstName = "Riya",
            LastName = "Sharma",
            Grade = "12",
            Section = "A",
            EducationLevel = educationLevel,
            ProfileImageUrl = photoUrl
        };

        public static ProgramInfo Program(int id = 1, string name = "BSc Computer Science", int level = Undergraduate, int deadlineInDays = 10, bool active = true) => new()
        {
            Id = id,
            Name = name,
            EligibleEducationLevel = level,
            ApplicationDeadline = Today.Date.AddDays(deadlineInDays),
            IsActive = active,
            IsAcceptingApplications = active && deadlineInDays >= 0
        };

        public static DocumentInfo Document(int type, int status, string fileName = "file.pdf", string? notes = null) => new()
        {
            Id = Random.Shared.Next(1, 10_000),
            DocumentType = type,
            Status = status,
            FileName = fileName,
            ReviewNotes = notes,
            UploadedAt = Today.AddDays(-1)
        };

        public static ProgramApplicationInfo Application(int programId, int status, string? notes = null) => new()
        {
            Id = 99,
            ProgramId = programId,
            ProgramName = "BSc Computer Science",
            Status = status,
            AppliedAt = Today.AddDays(-2),
            ReviewNotes = notes
        };
    }

    public class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    public static class TestJwt
    {
        public const string Key = "test-signing-key-that-is-long-enough-for-hmac-sha256-0123456789abcdef";
        public const string Issuer = "DigitalSchoolManagementSystem";
        public const string Audience = "DigitalSchoolManagementSystemClient";

        // Mirrors the claims DigitalSchoolManagementSystem.Infrastructure.Security.JwtTokenService issues.
        public static string Create(int userId = 42, string role = "Student", string key = Key)
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.GivenName, "Riya"),
                new Claim(ClaimTypes.Role, role)
            };
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(Issuer, Audience, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: credentials);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
