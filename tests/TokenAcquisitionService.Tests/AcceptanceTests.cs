namespace TokenAcquisitionService.Tests;

public sealed class AcceptanceTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(6, 5)]
    [InlineData(20, 5)]
    [InlineData(34, 4)]
    [InlineData(37, 1)]
    [InlineData(38, 0)]
    [InlineData(40, 0)]
    public async Task Worked_1040_expiry_example_caps_promoted_L1(int minute, int expectedTtlMinutes)
    {
        // Arrange: Set the worked example's clock, expiry, safety buffer, and cache caps.
        using var h = new Harness(new()
        {
            SafetyBuffer = TimeSpan.FromMinutes(2),
            L1MaximumTtl = TimeSpan.FromMinutes(5),
            L2MaximumTtl = TimeSpan.FromMinutes(60),
            BestEffortL2 = false
        });
        var start = h.Time.GetUtcNow().AddHours(10);
        h.Time.SetUtcNow(start);
        var token = new TokenResult("example", start.AddMinutes(40));
        h.Sdk.Handler = (_, _) => Task.FromResult(token);

        // Act: Acquire the token and inspect its initial L2 residence deadline.
        await h.Service.AcquireAsync(Harness.Request());
        var entry = await h.Store.GetAsync(Harness.Request().CacheKey, default);

        // Assert: L2 stops at the safety cutoff, not its configured sixty-minute maximum.
        Assert.Equal(start.AddMinutes(38), entry!.CacheUntil);

        // Arrange: Advance to the theory's time point and create an empty L1.
        h.Time.Advance(TimeSpan.FromMinutes(minute));
        using var restarted = h.NewService();
        if (expectedTtlMinutes == 0)
        {
            // Act & Assert: At or beyond the cutoff, acquisition must reject the SDK result.
            await Assert.ThrowsAsync<UnusableTokenException>(() => restarted.AcquireAsync(Harness.Request()));
            return;
        }
        // Act & Assert: Before the cutoff, the restarted service can promote the L2 token.
        Assert.Equal(token, await restarted.AcquireAsync(Harness.Request()));

        // Arrange: Make any L2 lookup fail so subsequent success proves L1 is still usable.
        h.Cache.Read = (_, _) => throw new IOException("probe local expiration");
        h.Time.Advance(TimeSpan.FromMinutes(expectedTtlMinutes) - TimeSpan.FromTicks(1));

        // Act & Assert: L1 works on its last fresh tick but requires L2 at its exact deadline.
        Assert.Equal(token, await restarted.AcquireAsync(Harness.Request()));
        h.Time.Advance(TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<IOException>(() => restarted.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task L1_avoids_L2_and_sdk_and_L2_hit_repopulates_L1()
    {
        // Arrange: Count shared-cache reads and writes around the real cache simulation.
        using var h = new Harness();
        var reads = 0;
        var writes = 0;
        h.Cache.Read = (key, ct) =>
        {
            Interlocked.Increment(ref reads);
            return h.Store.GetAsync(key, ct);
        };
        h.Cache.Write = (key, entry, ct) =>
        {
            Interlocked.Increment(ref writes);
            return h.Store.SetAsync(key, entry, ct);
        };
        var request = Harness.Request();

        // Act: Fill both caches through a genuine miss.
        var token = await h.Service.AcquireAsync(request);

        // Act & Assert: A repeat request uses L1 without additional L2 or SDK work.
        Assert.Equal(token, await h.Service.AcquireAsync(request));
        Assert.Equal(1, reads);
        Assert.Equal(1, writes);
        Assert.Equal(1, h.Sdk.Calls);

        // Arrange: Start a second service with empty L1 and the same L2.
        using var second = h.NewService();

        // Act & Assert: The first lookup promotes L2; the second uses that populated L1.
        Assert.Equal(token, await second.AcquireAsync(request));
        Assert.Equal(token, await second.AcquireAsync(request));
        Assert.Equal(2, reads);
        Assert.Equal(1, writes);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Application_only_and_delegated_contexts_remain_distinct()
    {
        // Arrange: Use the same tenant, client, authority, and scopes with different authentication contexts.
        using var h = new Harness();
        var app = new TokenRequest("tenant", "client", null, "https://issuer", "client-credentials", ["read"]);
        var user = new TokenRequest("tenant", "client", "alice", "https://issuer", "delegated", ["read"]);

        // Assert: Application-only and delegated requests cannot share a canonical key.
        Assert.NotEqual(app.CacheKey, user.CacheKey);

        // Act: Acquire both contexts through the same service.
        await h.Service.AcquireAsync(app);
        await h.Service.AcquireAsync(user);

        // Assert: Each context required its own SDK acquisition.
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Pre_canceled_call_does_not_read_caches_or_acquire()
    {
        // Arrange: Cancel before invocation and make any attempted L2 read fail the test.
        using var h = new Harness();
        h.Cache.Read = (_, _) => throw new InvalidOperationException("must not run");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert: Cancellation propagates before cache access or SDK acquisition.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.AcquireAsync(Harness.Request(), cts.Token));
        Assert.Equal(0, h.Sdk.Calls);
    }

    [Fact]
    public async Task Sdk_result_expiring_during_acquisition_is_rejected()
    {
        // Arrange: Hold a short-lived SDK result; extend the deadline to test expiry rather than timeout.
        using var h = new Harness(resilience: new() { AcquisitionTimeout = TimeSpan.FromMinutes(10) });
        var issued = h.Token(minutes: 2);
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return issued;
        };

        // Act: Start acquisition, expire the token, and then release the SDK response.
        var call = h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromMinutes(2));
        release.SetResult();

        // Assert: The delayed result is rejected and never persisted.
        await Assert.ThrowsAsync<UnusableTokenException>(() => call);
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task Sdk_failure_during_L2_outage_is_not_hidden()
    {
        // Arrange: Make L2 unavailable and the SDK fail authentication.
        using var h = new Harness();
        h.Cache.Read = (_, _) => throw new IOException();
        h.Sdk.Handler = (_, _) => throw new UnauthorizedAccessException("authentication failed");

        // Act & Assert: Cache fallback does not hide SDK failure or populate an invalid entry.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task One_hundred_failing_callers_are_released_and_later_recovery_succeeds()
    {
        // Arrange: Hold a failing SDK response while same-key callers contend.
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            throw new InvalidOperationException("controlled failure");
        };

        // Act: Start all callers before allowing SDK failures to proceed.
        var calls = Enumerable.Range(0, 100).Select(_ => h.Service.AcquireAsync(Harness.Request())).ToArray();
        release.SetResult();

        // Assert: Every caller exits with failure and no token poisons L2.
        foreach (var call in calls)
            await Assert.ThrowsAsync<InvalidOperationException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));

        // Arrange: Restore a successful SDK response.
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());

        // Act & Assert: A later request completes without a stranded gate.
        await h.Service.AcquireAsync(Harness.Request());
    }

    [Fact]
    public async Task Parallel_tenants_receive_only_their_own_tokens()
    {
        // Arrange: Return a tenant-specific token after a shared release signal.
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token(request.Tenant);
        };

        // Act: Acquire one hundred tenant contexts concurrently and release their responses.
        var calls = Enumerable.Range(0, 100).Select(i =>
            h.Service.AcquireAsync(Harness.Request(tenant: $"tenant-{i}"))).ToArray();
        release.SetResult();
        var tokens = await Task.WhenAll(calls);

        // Assert: Every caller receives only its own tenant's token.
        for (var i = 0; i < tokens.Length; i++)
            Assert.Equal($"tenant-{i}", tokens[i].AccessToken);
    }

    [Fact]
    public async Task Actual_thread_pool_contention_preserves_singleflight()
    {
        // Arrange: Prepare signals that align one hundred thread-pool callers at the starting point.
        using var h = new Harness();
        var ready = Harness.Signal();
        var start = Harness.Signal();
        var count = 0;

        // Act: Schedule all callers, wait until they are ready, then let them compete for the same key.
        var calls = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref count) == 100) ready.SetResult();
            await start.Task;
            return await h.Service.AcquireAsync(Harness.Request());
        })).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        start.SetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: Actual thread-pool contention still produces one successful SDK acquisition.
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 1, 0)]
    public void Invalid_expiration_options_are_rejected(int buffer, int l1, int l2)
    {
        // Arrange: Theory inputs supply a negative buffer or a zero cache TTL.
        // Act & Assert: Constructing the service with those options must reject the configuration.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Harness(new()
        {
            SafetyBuffer = TimeSpan.FromSeconds(buffer),
            L1MaximumTtl = TimeSpan.FromSeconds(l1),
            L2MaximumTtl = TimeSpan.FromSeconds(l2)
        }));
    }
}
