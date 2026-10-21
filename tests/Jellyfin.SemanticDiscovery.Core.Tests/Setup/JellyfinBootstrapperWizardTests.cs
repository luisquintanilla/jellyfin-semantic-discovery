using System.Net;
using System.Text;
using Jellyfin.SemanticDiscovery.Setup;

namespace Jellyfin.SemanticDiscovery.Core.Tests.Setup;

public sealed class JellyfinBootstrapperWizardTests
{
    [Fact]
    public async Task BootstrapAsyncResumesPartiallyCompletedWizardWithoutResettingUser()
    {
        WizardHandler handler = new(authenticationSucceeds: true);
        using HttpClient client = new(handler);
        JellyfinBootstrapper bootstrapper = CreateBootstrapper(client);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => bootstrapper.BootstrapAsync(CancellationToken.None));

        Assert.DoesNotContain(
            handler.Requests,
            request => request == "POST Startup/User");
        Assert.Contains("POST Startup/Configuration", handler.Requests);
        Assert.Contains("POST Startup/RemoteAccess", handler.Requests);
        Assert.Contains("POST Startup/Complete", handler.Requests);
    }

    [Fact]
    public async Task BootstrapAsyncRefusesToOverwriteExistingUnknownPassword()
    {
        WizardHandler handler = new(authenticationSucceeds: false);
        using HttpClient client = new(handler);
        JellyfinBootstrapper bootstrapper = CreateBootstrapper(client);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => bootstrapper.BootstrapAsync(CancellationToken.None));

        Assert.Contains("refusing to overwrite", exception.Message, StringComparison.Ordinal);
        Assert.Contains("POST Startup/User", handler.Requests);
        Assert.DoesNotContain("POST Startup/Complete", handler.Requests);
    }

    private static JellyfinBootstrapper CreateBootstrapper(HttpClient client) =>
        new(
            client,
            new JellyfinBootstrapOptions
            {
                BaseAddress = new Uri("http://jellyfin.test/"),
                AdminPassword = "configured-secret",
                ReadyTimeout = TimeSpan.FromSeconds(1),
            });

    private sealed class WizardHandler(bool authenticationSucceeds)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.PathAndQuery.TrimStart('/');
            Requests.Add($"{request.Method} {path}");
            return Task.FromResult(CreateResponse(request.Method, path));
        }

        private HttpResponseMessage CreateResponse(HttpMethod method, string path)
        {
            if (method == HttpMethod.Get && path == "System/Info/Public")
            {
                return Json("""{"StartupWizardCompleted":false}""");
            }
            if (method == HttpMethod.Get && path == "Startup/User")
            {
                return Json("""{"Name":"semantic-admin"}""");
            }
            if (method == HttpMethod.Post && path == "Users/AuthenticateByName")
            {
                return authenticationSucceeds
                    ? Json("""{"AccessToken":"test-token"}""")
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (method == HttpMethod.Post && path == "Startup/User")
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            }
            if (method == HttpMethod.Post &&
                path is "Startup/Configuration" or
                    "Startup/RemoteAccess" or
                    "Startup/Complete")
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };
    }
}
