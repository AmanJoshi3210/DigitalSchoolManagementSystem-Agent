using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DigitalSchoolManagementSystem.Agent.Api.Backend
{
    // Read-only view of the main DSMS API, always on behalf of the student who is chatting.
    public interface IDsmsBackend
    {
        Task<StudentProfile?> GetMyProfileAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken cancellationToken = default);
        Task<ProgramInfo?> GetProgramAsync(int programId, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ProgramApplicationInfo>> GetMyApplicationsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<DocumentInfo>> GetMyDocumentsAsync(CancellationToken cancellationToken = default);
    }

    public class BackendException(string message, HttpStatusCode? statusCode = null) : Exception(message)
    {
        public HttpStatusCode? StatusCode { get; } = statusCode;
    }

    // The agent never has credentials of its own: every call forwards the caller's bearer token,
    // so the backend applies exactly the same authorization it would for the student directly.
    public class DsmsApiClient(HttpClient http, IHttpContextAccessor httpContextAccessor) : IDsmsBackend
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        public Task<StudentProfile?> GetMyProfileAsync(CancellationToken cancellationToken = default) =>
            GetAsync<StudentProfile>("students/me", cancellationToken);

        public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken cancellationToken = default) =>
            await GetAsync<List<ProgramInfo>>("programs", cancellationToken) ?? [];

        public Task<ProgramInfo?> GetProgramAsync(int programId, CancellationToken cancellationToken = default) =>
            GetAsync<ProgramInfo>($"programs/{programId}", cancellationToken);

        public async Task<IReadOnlyList<ProgramApplicationInfo>> GetMyApplicationsAsync(CancellationToken cancellationToken = default) =>
            await GetAsync<List<ProgramApplicationInfo>>("programs/me/applications", cancellationToken) ?? [];

        public async Task<IReadOnlyList<DocumentInfo>> GetMyDocumentsAsync(CancellationToken cancellationToken = default) =>
            await GetAsync<List<DocumentInfo>>("documents/me", cancellationToken) ?? [];

        private async Task<T?> GetAsync<T>(string relativePath, CancellationToken cancellationToken) where T : class
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);

            var authorization = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(authorization) || !AuthenticationHeaderValue.TryParse(authorization, out var header))
                throw new BackendException("No student session is available for this request.", HttpStatusCode.Unauthorized);
            request.Headers.Authorization = header;

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new BackendException($"The school portal API is unreachable: {ex.Message}");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                if (!response.IsSuccessStatusCode)
                    throw new BackendException($"The school portal API returned {(int)response.StatusCode}.", response.StatusCode);

                return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            }
        }
    }
}
