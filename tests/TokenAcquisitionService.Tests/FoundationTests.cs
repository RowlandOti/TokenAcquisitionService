namespace TokenAcquisitionService.Tests;

public sealed class FoundationTests
{
    [Theory]
    [InlineData("tenant")]
    [InlineData("client")]
    [InlineData("principal")]
    [InlineData("authority")]
    [InlineData("flow")]
    [InlineData("scopes")]
    [InlineData("parameters")]
    public async Task Security_context_is_isolated(string dimension)
    {
        using var h = new Harness();
        var first = Harness.Request();
        var other = Harness.Request(
            principal: dimension == "principal" ? "bob" : "alice",
            tenant: dimension == "tenant" ? "other" : "tenant",
            client: dimension == "client" ? "other" : "client",
            authority: dimension == "authority" ? "https://other.example" : first.Authority,
            flow: dimension == "flow" ? "client-credentials" : "delegated",
            scopes: dimension == "scopes" ? ["write"] : ["read"],
            parameters: dimension == "parameters" ? [new("claims", "different")] : null);
        Assert.NotEqual(first.CacheKey, other.CacheKey);
        await h.Service.AcquireAsync(first);
        await h.Service.AcquireAsync(other);
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public void Canonical_identity_is_order_independent_unambiguous_and_immutable()
    {
        string[] scopes = ["write", "read", "read"];
        var first = Harness.Request(scopes: scopes, parameters: [new("z", "1"), new("a", "2")]);
        var second = Harness.Request(scopes: ["read", "write"], parameters: [new("a", "2"), new("z", "1")]);
        scopes[0] = "changed";
        Assert.Equal(first.CacheKey, second.CacheKey);
        Assert.NotEqual(Harness.Request(tenant: "a|b", client: "c").CacheKey,
            Harness.Request(tenant: "a", client: "b|c").CacheKey);
        Assert.NotEqual(Harness.Request(scopes: ["Read"]).CacheKey, Harness.Request(scopes: ["read"]).CacheKey);
    }

    [Fact]
    public async Task One_hundred_same_key_callers_use_one_acquisition()
    {
        using var h = new Harness();
        var entered = Harness.Signal();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var tasks = Enumerable.Range(0, 100).Select(_ => h.Service.AcquireAsync(Harness.Request())).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Sdk.Calls);
        release.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.Equal("test-token", result.AccessToken));
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task One_hundred_different_keys_acquire_independently()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var tasks = Enumerable.Range(0, 100)
            .Select(i => h.Service.AcquireAsync(Harness.Request(principal: $"user-{i}"))).ToArray();
        Assert.Equal(100, h.Sdk.Calls);
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Sdk_failure_does_not_poison_cache_or_gate()
    {
        using var h = new Harness();
        var entered = Harness.Signal();
        var fail = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            entered.TrySetResult();
            await fail.Task.WaitAsync(ct);
            throw new InvalidOperationException("issuer failed");
        };
        var first = h.Service.AcquireAsync(Harness.Request());
        await entered.Task;
        var waiting = h.Service.AcquireAsync(Harness.Request());
        fail.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_cancel_owner_or_block_later_callers()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var owner = h.Service.AcquireAsync(Harness.Request());
        using var cts = new CancellationTokenSource();
        var waiting = h.Service.AcquireAsync(Harness.Request(), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        release.SetResult();
        await owner;
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Canceled_initiator_does_not_cancel_shared_acquisition()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        using var cts = new CancellationTokenSource();
        var owner = h.Service.AcquireAsync(Harness.Request(), cts.Token);
        var waiting = h.Service.AcquireAsync(Harness.Request());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.False(waiting.IsCompleted);
        release.SetResult();
        Assert.Equal("test-token", (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).AccessToken);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task L1_works_during_L2_outage_and_best_effort_misses_fall_back()
    {
        using var h = new Harness();
        await h.Service.AcquireAsync(Harness.Request());
        h.Cache.Read = (_, _) => throw new IOException("offline");
        h.Cache.Write = (_, _, _) => throw new IOException("offline");
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
        await h.Service.AcquireAsync(Harness.Request(principal: "bob"));
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Strict_L2_policy_propagates_read_and_write_failures()
    {
        using var h = new Harness(new() { BestEffortL2 = false });
        h.Cache.Read = (_, _) => throw new IOException("read");
        await Assert.ThrowsAsync<IOException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Equal(0, h.Sdk.Calls);
        h.Cache.Read = null;
        h.Cache.Write = (_, _, _) => throw new IOException("write");
        await Assert.ThrowsAsync<IOException>(() => h.Service.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task L2_cancellation_is_never_swallowed()
    {
        using var h = new Harness();
        h.Cache.Read = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Equal(0, h.Sdk.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Safety_boundary_is_strict(int secondsBeyondBuffer)
    {
        using var h = new Harness();
        var token = new TokenResult("boundary", h.Time.GetUtcNow() + h.Options.SafetyBuffer
            + TimeSpan.FromSeconds(secondsBeyondBuffer));
        h.Sdk.Handler = (_, _) => Task.FromResult(token);
        if (secondsBeyondBuffer <= 0)
            await Assert.ThrowsAsync<UnusableTokenException>(() => h.Service.AcquireAsync(Harness.Request()));
        else
            Assert.Equal(token, await h.Service.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task Empty_sdk_token_is_rejected_without_caching()
    {
        using var h = new Harness();
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token(""));
        await Assert.ThrowsAsync<UnusableTokenException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task Token_expiring_during_L2_read_is_not_returned()
    {
        using var h = new Harness(resilience: new()
        {
            AcquisitionTimeout = TimeSpan.FromMinutes(10),
            L2Timeout = TimeSpan.FromMinutes(5)
        });
        var token = h.Token("old", 2);
        h.Cache.Read = (key, _) =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            return Task.FromResult<CacheEnvelope?>(new(key, token, token.ExpiresAt));
        };
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Token_expiring_during_write_is_not_returned_or_promoted()
    {
        using var h = new Harness(resilience: new()
        {
            AcquisitionTimeout = TimeSpan.FromMinutes(10),
            L2Timeout = TimeSpan.FromMinutes(5)
        });
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token(minutes: 2));
        h.Cache.Write = (_, _, _) =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<UnusableTokenException>(() => h.Service.AcquireAsync(Harness.Request()));
        h.Cache.Write = null;
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task L1_and_L2_ttls_are_capped_and_promotion_does_not_reset_L2_lifetime()
    {
        using var h = new Harness(new()
        {
            SafetyBuffer = TimeSpan.FromMinutes(1),
            L1MaximumTtl = TimeSpan.FromMinutes(2),
            L2MaximumTtl = TimeSpan.FromMinutes(4)
        });
        await h.Service.AcquireAsync(Harness.Request());
        var key = Harness.Request().CacheKey;
        Assert.Equal(h.Time.GetUtcNow().AddMinutes(4), (await h.Store.GetAsync(key, default))!.CacheUntil);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Near_expiry_L2_promotion_recalculates_usable_lifetime()
    {
        using var h = new Harness();
        var key = Harness.Request().CacheKey;
        var token = h.Token("remote", 2);
        await h.Store.SetAsync(key, new(key, token, token.ExpiresAt), default);
        await h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
    }

    [Fact]
    public async Task Another_users_envelope_is_rejected_even_under_the_requested_key()
    {
        using var h = new Harness();
        var alice = Harness.Request();
        var bob = Harness.Request(principal: "bob");
        await h.Store.SetAsync(alice.CacheKey,
            new(bob.CacheKey, h.Token("bob-token"), h.Time.GetUtcNow().AddMinutes(10)), default);
        Assert.Equal("test-token", (await h.Service.AcquireAsync(alice)).AccessToken);
    }

    [Fact]
    public async Task Restart_rehydrates_L1_from_shared_L2_without_sdk()
    {
        using var h = new Harness();
        await h.Service.AcquireAsync(Harness.Request());
        using var restarted = h.NewService();
        await restarted.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Two_instances_can_duplicate_acquisition_and_older_write_can_win()
    {
        using var h = new Harness();
        using var other = h.NewService();
        var firstEntered = Harness.Signal();
        var releaseFirst = Harness.Signal();
        var older = h.Token("older", 20);
        var newer = h.Token("newer", 40);
        h.Sdk.Handler = async (_, ct) =>
        {
            if (h.Sdk.Calls == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
                return older;
            }
            return newer;
        };
        var lateWriter = h.Service.AcquireAsync(Harness.Request());
        await firstEntered.Task;
        Assert.Equal(newer, await other.AcquireAsync(Harness.Request()));
        releaseFirst.SetResult();
        Assert.Equal(older, await lateWriter);
        Assert.Equal(2, h.Sdk.Calls);
        var entry = await h.Store.GetAsync(Harness.Request().CacheKey, default);
        Assert.Equal(older, entry!.Token);
        Assert.True(entry.CacheUntil <= older.ExpiresAt - h.Options.SafetyBuffer);
        // The second instance retains its newer L1; there is no cross-instance coherence.
        Assert.Equal(newer, await other.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task Expired_local_entry_is_rechecked_after_waiting()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token(minutes: 2);
        };
        var first = h.Service.AcquireAsync(Harness.Request());
        var waiting = h.Service.AcquireAsync(Harness.Request());
        release.SetResult();
        await Task.WhenAll(first, waiting);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public void Token_diagnostics_redact_the_secret()
    {
        Assert.DoesNotContain("sensitive-secret", new TokenResult("sensitive-secret", DateTimeOffset.MaxValue).ToString());
    }
}
