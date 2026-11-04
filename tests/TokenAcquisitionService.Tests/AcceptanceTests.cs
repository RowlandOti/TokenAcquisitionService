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
        await h.Service.AcquireAsync(Harness.Request());
        var entry = await h.Store.GetAsync(Harness.Request().CacheKey, default);
        Assert.Equal(start.AddMinutes(38), entry!.CacheUntil);
        h.Time.Advance(TimeSpan.FromMinutes(minute));
        using var restarted = h.NewService();
        if (expectedTtlMinutes == 0)
        {
            await Assert.ThrowsAsync<UnusableTokenException>(() => restarted.AcquireAsync(Harness.Request()));
            return;
        }
        Assert.Equal(token, await restarted.AcquireAsync(Harness.Request()));
        h.Cache.Read = (_, _) => throw new IOException("probe local expiration");
        h.Time.Advance(TimeSpan.FromMinutes(expectedTtlMinutes) - TimeSpan.FromTicks(1));
        Assert.Equal(token, await restarted.AcquireAsync(Harness.Request()));
        h.Time.Advance(TimeSpan.FromTicks(1));
        await Assert.ThrowsAsync<IOException>(() => restarted.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task L1_avoids_L2_and_sdk_and_L2_hit_repopulates_L1()
    {
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
        var token = await h.Service.AcquireAsync(request);
        Assert.Equal(token, await h.Service.AcquireAsync(request));
        Assert.Equal(1, reads);
        Assert.Equal(1, writes);
        Assert.Equal(1, h.Sdk.Calls);
        using var second = h.NewService();
        Assert.Equal(token, await second.AcquireAsync(request));
        Assert.Equal(token, await second.AcquireAsync(request));
        Assert.Equal(2, reads);
        Assert.Equal(1, writes);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Application_only_and_delegated_contexts_remain_distinct()
    {
        using var h = new Harness();
        var app = new TokenRequest("tenant", "client", null, "https://issuer", "client-credentials", ["read"]);
        var user = new TokenRequest("tenant", "client", "alice", "https://issuer", "delegated", ["read"]);
        Assert.NotEqual(app.CacheKey, user.CacheKey);
        await h.Service.AcquireAsync(app);
        await h.Service.AcquireAsync(user);
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Pre_canceled_call_does_not_read_caches_or_acquire()
    {
        using var h = new Harness();
        h.Cache.Read = (_, _) => throw new InvalidOperationException("must not run");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.AcquireAsync(Harness.Request(), cts.Token));
        Assert.Equal(0, h.Sdk.Calls);
    }

    [Fact]
    public async Task Sdk_result_expiring_during_acquisition_is_rejected()
    {
        using var h = new Harness(resilience: new() { AcquisitionTimeout = TimeSpan.FromMinutes(10) });
        var issued = h.Token(minutes: 2);
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return issued;
        };
        var call = h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromMinutes(2));
        release.SetResult();
        await Assert.ThrowsAsync<UnusableTokenException>(() => call);
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task Sdk_failure_during_L2_outage_is_not_hidden()
    {
        using var h = new Harness();
        h.Cache.Read = (_, _) => throw new IOException();
        h.Sdk.Handler = (_, _) => throw new UnauthorizedAccessException("authentication failed");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task One_hundred_failing_callers_are_released_and_later_recovery_succeeds()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            throw new InvalidOperationException("controlled failure");
        };
        var calls = Enumerable.Range(0, 100).Select(_ => h.Service.AcquireAsync(Harness.Request())).ToArray();
        release.SetResult();
        foreach (var call in calls)
            await Assert.ThrowsAsync<InvalidOperationException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());
        await h.Service.AcquireAsync(Harness.Request());
    }

    [Fact]
    public async Task Parallel_tenants_receive_only_their_own_tokens()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token(request.Tenant);
        };
        var calls = Enumerable.Range(0, 100).Select(i =>
            h.Service.AcquireAsync(Harness.Request(tenant: $"tenant-{i}"))).ToArray();
        release.SetResult();
        var tokens = await Task.WhenAll(calls);
        for (var i = 0; i < tokens.Length; i++)
            Assert.Equal($"tenant-{i}", tokens[i].AccessToken);
    }

    [Fact]
    public async Task Actual_thread_pool_contention_preserves_singleflight()
    {
        using var h = new Harness();
        var ready = Harness.Signal();
        var start = Harness.Signal();
        var count = 0;
        var calls = Enumerable.Range(0, 100).Select(_ => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref count) == 100) ready.SetResult();
            await start.Task;
            return await h.Service.AcquireAsync(Harness.Request());
        })).ToArray();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        start.SetResult();
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Theory]
    [InlineData(-1, 1, 1)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 1, 0)]
    public void Invalid_expiration_options_are_rejected(int buffer, int l1, int l2)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Harness(new()
        {
            SafetyBuffer = TimeSpan.FromSeconds(buffer),
            L1MaximumTtl = TimeSpan.FromSeconds(l1),
            L2MaximumTtl = TimeSpan.FromSeconds(l2)
        }));
    }
}
