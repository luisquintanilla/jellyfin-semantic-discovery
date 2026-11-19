using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.SemanticDiscovery.Extraction.Api;
using Jellyfin.SemanticDiscovery.Extraction.Contracts;
using Jellyfin.SemanticDiscovery.Extraction.Jobs;
using Jellyfin.SemanticDiscovery.Extraction.Worker;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.SemanticDiscovery.Extraction.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public async Task PostReturnsAcceptedStatusUriAndExplicitQueueDurability()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExtractionJob job = new(
            "job123",
            "manifest-item",
            new string('a', 64),
            ExtractionProfile.Hybrid,
            "qwen3-vl:8b-instruct",
            ExtractionJobState.Pending,
            now,
            now);
        await using TestApplication application = await TestApplication.StartAsync(
            new FakeCoordinator(job));

        using HttpResponseMessage response = await application.Client.PostAsJsonAsync(
            "/extractions",
            new CreateExtractionRequest(
                job.ManifestItemId,
                job.SourceSha256,
                job.ExtractionProfile));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/extractions/job123", response.Headers.Location?.ToString());
        ExtractionAcceptedResponse accepted =
            await response.Content.ReadFromJsonAsync<ExtractionAcceptedResponse>()
            ?? throw new InvalidDataException("The response body was empty.");
        Assert.Equal(job.JobId, accepted.JobId);
        Assert.False(accepted.QueueDurable);
        Assert.Contains(
            "not durable",
            accepted.QueueDurability,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostRejectsSourcePathsOutsideTheManifestOnlyContract()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExtractionJob job = new(
            "job123",
            "manifest-item",
            new string('a', 64),
            ExtractionProfile.Text,
            "qwen3-vl:8b-instruct",
            ExtractionJobState.Pending,
            now,
            now);
        await using TestApplication application = await TestApplication.StartAsync(
            new FakeCoordinator(job));
        using StringContent body = new(
            """
            {
              "manifestItemId": "manifest-item",
              "expectedSourceSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "extractionProfile": "text",
              "sourcePath": "..\\outside.pdf"
            }
            """,
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await application.Client.PostAsync(
            "/extractions",
            body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetAndDeleteExposePersistedStatusAndCancellation()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExtractionJob job = new(
            "job123",
            "manifest-item",
            new string('a', 64),
            ExtractionProfile.Vision,
            "qwen3-vl:8b-instruct",
            ExtractionJobState.Running,
            now,
            now);
        FakeCoordinator coordinator = new(job);
        await using TestApplication application =
            await TestApplication.StartAsync(coordinator);

        using HttpResponseMessage getResponse =
            await application.Client.GetAsync("/extractions/job123");
        using HttpResponseMessage deleteResponse =
            await application.Client.DeleteAsync("/extractions/job123");

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, deleteResponse.StatusCode);
        Assert.True(coordinator.CancelCalled);
    }

    private sealed class TestApplication(
        WebApplication application,
        HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public static async ValueTask<TestApplication> StartAsync(
            IExtractionCoordinator coordinator)
        {
            WebApplicationBuilder builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.UnmappedMemberHandling =
                    JsonUnmappedMemberHandling.Disallow;
                options.SerializerOptions.Converters.Add(
                    new JsonStringEnumConverter());
            });
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddSingleton<IExtractionJobChannel>(
                new BoundedExtractionJobChannel(4));
            ReadinessState readiness = new();
            readiness.Ready();
            builder.Services.AddSingleton<IReadinessState>(readiness);
            WebApplication application = builder.Build();
            application.MapExtractionEndpoints();
            await application.StartAsync();
            IServer server = application.Services.GetRequiredService<IServer>();
            string address = server.Features
                .Get<IServerAddressesFeature>()!
                .Addresses.Single();
            return new TestApplication(
                application,
                new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }

    private sealed class FakeCoordinator(ExtractionJob job) :
        IExtractionCoordinator
    {
        private ExtractionJob _job = job;

        public bool CancelCalled { get; private set; }

        public ValueTask<ExtractionJob> SubmitAsync(
            CreateExtractionRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(_job);

        public ValueTask<ExtractionJob?> FindAsync(
            string jobId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ExtractionJob?>(
                string.Equals(jobId, _job.JobId, StringComparison.Ordinal)
                    ? _job
                    : null);

        public ValueTask<ExtractionJob?> CancelAsync(
            string jobId,
            CancellationToken cancellationToken)
        {
            CancelCalled = true;
            _job = _job with
            {
                State = ExtractionJobState.CancellationRequested,
            };
            return ValueTask.FromResult<ExtractionJob?>(_job);
        }
    }
}
