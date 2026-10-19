using DeploymentPortal.Data;
using DeploymentPortal.Models;
using DeploymentPortal.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DeploymentPortal.Tests;

public sealed class DemoTargetHealthCheckTests
{
    [Theory]
    [InlineData(true, true, 200, "http", "completed")]
    [InlineData(true, false, 200, "http", "failed")]
    [InlineData(false, false, 302, "http", "failed")]
    [InlineData(false, false, 503, "http", "failed")]
    [InlineData(false, false, null, "tls", "failed")]
    [InlineData(false, false, null, "timeout", "pending")]
    [InlineData(false, false, null, "network", "pending")]
    [InlineData(false, false, null, null, "pending")]
    public async Task DemoUsesProtectedTargetConnectionAndOnlyVerified200Completes(bool success, bool verified, int? status, string? reason, string expected)
    {
        using var db = Database();
        db.Servers.Add(new Server { HostNameOrIp = "deployment-test.vitonta.com", Username = "protected-user", WinRmPort = 5985, UseSsl = false });
        await db.SaveChangesAsync();
        var credentials = new Credentials();
        var remote = new Remote(new(success, "SECRET must not be displayed", Verified: verified, Verification: reason, HealthHttpStatus: status));
        var target = new DemoTargetHealthCheck(db, new Registry(), credentials, remote);
        var check = new PipelineHealthCheck(new ConfigurationBuilder().Build(), new NoLocalHttp(), target);
        var result = await check.Check("Demo", default);
        Assert.Equal(expected, result.State);
        Assert.Equal(DemoTargetHealthCheck.Url, result.Url);
        Assert.DoesNotContain("SECRET", result.Detail);
        Assert.Equal(1, credentials.Calls);
        Assert.Equal(1, remote.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingAmbiguousOrUnavailableConnectionNeverRunsRemoteCheck(bool duplicate, bool credentialFailure)
    {
        using var db = Database();
        if (duplicate || credentialFailure)
            db.Servers.Add(new Server { HostNameOrIp = "deployment-test.vitonta.com", WinRmPort = 5985 });
        if (duplicate) db.Servers.Add(new Server { HostNameOrIp = "deployment-test.vitonta.com", WinRmPort = 5986 });
        await db.SaveChangesAsync();
        var remote = new Remote(new(true, "", Verified: true, HealthHttpStatus: 200));
        var result = await new DemoTargetHealthCheck(db, new Registry(), new Credentials { Fail = credentialFailure }, remote).Check(default);
        Assert.Equal("pending", result.State);
        Assert.Contains("Unverified", result.Detail);
        Assert.DoesNotContain("SECRET", result.Detail);
        Assert.Equal(0, remote.Calls);
    }

    private static ApplicationDbContext Database() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class Registry : ISiteRegistry
    {
        private readonly RegistryApplication app = new() { Key = "Demo", Destinations = [new() { ServerHostNameOrIp = "deployment-test.vitonta.com" }] };
        public RegistryApplication? Find(string key) => key == "Demo" ? app : null;
        public IReadOnlyList<RegistryApplication> List() => [app];
    }
    private sealed class Credentials : ICredentialProvider
    {
        public int Calls;
        public bool Fail;
        public Task<RemoteCredential> GetAsync(Server server)
        {
            Calls++;
            if (Fail) throw new InvalidOperationException("SECRET");
            return Task.FromResult(new RemoteCredential(server.Username, "SECRET"));
        }
    }
    private sealed class Remote(RemoteResult result) : IPowerShellService
    {
        public int Calls;
        public Task<RemoteResult> ExecuteAsync(RemoteOperation operation, Server server, RemoteCredential credential, Deployment? deployment = null)
        {
            Calls++;
            Assert.Equal(RemoteOperation.VerifyDemoHealth, operation);
            Assert.Equal("deployment-test.vitonta.com", server.HostNameOrIp);
            Assert.Equal(5985, server.WinRmPort);
            Assert.False(server.UseSsl);
            Assert.Equal(server.Username, credential.Username);
            Assert.Null(deployment);
            return Task.FromResult(result);
        }
    }
    private sealed class NoLocalHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Demo HTTPS GET must run on TARGET");
    }
}
