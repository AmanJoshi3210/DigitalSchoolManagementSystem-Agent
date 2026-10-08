using System.Net;
using System.Text;
using DigitalSchoolManagementSystem.Agent.Api.Backend;
using Microsoft.AspNetCore.Http;

namespace DigitalSchoolManagementSystem.Agent.Tests
{
    public class DsmsApiClientTests
    {
        private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
        {
            public HttpRequestMessage? LastRequest { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                LastRequest = request;
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }

        private static (DsmsApiClient Client, StubHandler Handler) Create(HttpStatusCode status, string body, string? authorization = "Bearer student-token")
        {
            var handler = new StubHandler(status, body);
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://backend/api/") };
            var context = new DefaultHttpContext();
            if (authorization is not null)
                context.Request.Headers.Authorization = authorization;
            return (new DsmsApiClient(http, new HttpContextAccessor { HttpContext = context }), handler);
        }

        [Fact]
        public async Task Forwards_the_students_bearer_token_to_the_api_path()
        {
            var (client, handler) = Create(HttpStatusCode.OK, """{ "id": 7, "firstName": "Riya", "educationLevel": 1, "profileImageUrl": null }""");

            var profile = await client.GetMyProfileAsync();

            Assert.Equal("http://backend/api/students/me", handler.LastRequest!.RequestUri!.ToString());
            Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
            Assert.Equal("student-token", handler.LastRequest.Headers.Authorization.Parameter);
            Assert.Equal("Riya", profile!.FirstName);
            Assert.Equal(1, profile.EducationLevel);
        }

        [Fact]
        public async Task Parses_numeric_enums_in_lists()
        {
            var (client, _) = Create(HttpStatusCode.OK, """[ { "id": 1, "documentType": 3, "status": 2, "fileName": "a.pdf", "reviewNotes": "Blurry" } ]""");

            var documents = await client.GetMyDocumentsAsync();

            var doc = Assert.Single(documents);
            Assert.Equal(EnumLabels.DocumentTypes.Certificate, doc.DocumentType);
            Assert.Equal(EnumLabels.ReviewStatus.Rejected, doc.Status);
        }

        [Fact]
        public async Task Not_found_returns_null()
        {
            var (client, _) = Create(HttpStatusCode.NotFound, "");

            Assert.Null(await client.GetProgramAsync(123));
        }

        [Fact]
        public async Task Server_error_throws_backend_exception()
        {
            var (client, _) = Create(HttpStatusCode.InternalServerError, "");

            var ex = await Assert.ThrowsAsync<BackendException>(() => client.GetProgramsAsync());
            Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        }

        [Fact]
        public async Task Without_a_student_token_nothing_is_sent()
        {
            var (client, handler) = Create(HttpStatusCode.OK, "{}", authorization: null);

            await Assert.ThrowsAsync<BackendException>(() => client.GetMyProfileAsync());
            Assert.Null(handler.LastRequest);
        }
    }
}
