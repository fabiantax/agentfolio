namespace AgentFolio.UnitTests;

using AgentFolio.Platform.Storage;
using System;
using Xunit;

/// <summary>
/// S3ObjectStore config contract (measured live against garage 2026-09-17):
/// self-hosted stores reject the us-east-1 sigv4 scope, so the region must be
/// explicit — a null region fails loudly at construction. Hermetic: uses the
/// 5-arg ctor directly, never touches process env (parallel test isolation).
/// </summary>
public sealed class S3ObjectStoreTests
{
    [Fact]
    public void ConstructorWithoutRegionThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new S3ObjectStore("http://127.0.0.1:3900", "agentfolio", "k", "s", null));
        Assert.Contains(S3ObjectStore.RegionEnvVar, ex.Message);
    }

    [Fact]
    public void ConstructorWithRegionBuildsClient()
    {
        using var store = new S3ObjectStore("http://127.0.0.1:3900", "agentfolio", "k", "s", "garage");
        Assert.NotNull(store);
    }
}
