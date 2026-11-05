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
        // Arrange: Change exactly one security-context dimension from the baseline request.
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

        // Assert: Distinct security contexts produce distinct keys.
        Assert.NotEqual(first.CacheKey, other.CacheKey);

        // Act: Acquire both contexts through the same caches.
        await h.Service.AcquireAsync(first);
        await h.Service.AcquireAsync(other);

        // Assert: The second context cannot reuse the first context's token.
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public void Canonical_identity_is_order_independent_unambiguous_and_immutable()
    {
        // Arrange: Build equivalent requests from reordered scopes and parameters.
        string[] scopes = ["write", "read", "read"];
        var first = Harness.Request(scopes: scopes, parameters: [new("z", "1"), new("a", "2")]);
        var second = Harness.Request(scopes: ["read", "write"], parameters: [new("a", "2"), new("z", "1")]);

        // Act: Mutate the original scope array after the request has snapshotted it.
        scopes[0] = "changed";

        // Assert: Equivalent keys remain equal, while delimiter collisions and case changes remain distinct.
        Assert.Equal(first.CacheKey, second.CacheKey);
        Assert.NotEqual(Harness.Request(tenant: "a|b", client: "c").CacheKey,
            Harness.Request(tenant: "a", client: "b|c").CacheKey);
        Assert.NotEqual(Harness.Request(scopes: ["Read"]).CacheKey, Harness.Request(scopes: ["read"]).CacheKey);
    }

    [Fact]
    public async Task One_hundred_same_key_callers_use_one_acquisition()
    {
        // Arrange: Hold the SDK response and signal when the first acquisition enters.
        using var h = new Harness();
        var entered = Harness.Signal();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return h.Token();
        };

        // Act: Start one hundred same-key callers while the first acquisition is held.
        var tasks = Enumerable.Range(0, 100).Select(_ => h.Service.AcquireAsync(Harness.Request())).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: Only one caller reaches the SDK while the other callers wait.
        Assert.Equal(1, h.Sdk.Calls);

        // Act: Complete the first fill and allow all waiting callers to finish.
        release.SetResult();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: All callers receive the token without another SDK acquisition.
        Assert.All(results, result => Assert.Equal("test-token", result.AccessToken));
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task One_hundred_different_keys_acquire_independently()
    {
        // Arrange: Hold SDK responses so overlapping acquisitions can be observed.
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };

        // Act: Start one hundred distinct principal contexts.
        var tasks = Enumerable.Range(0, 100)
            .Select(i => h.Service.AcquireAsync(Harness.Request(principal: $"user-{i}"))).ToArray();

        // Assert: Every independent key reaches the SDK before any response is released.
        Assert.Equal(100, h.Sdk.Calls);

        // Act & Assert: Release the responses and verify all operations complete without deadlock.
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Sdk_failure_does_not_poison_cache_or_gate()
    {
        // Arrange: Hold a failing SDK response long enough for a second caller to wait.
        using var h = new Harness();
        var entered = Harness.Signal();
        var fail = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            entered.TrySetResult();
            await fail.Task.WaitAsync(ct);
            throw new InvalidOperationException("issuer failed");
        };

        // Act: Start the owner and waiter, then trigger SDK failure.
        var first = h.Service.AcquireAsync(Harness.Request());
        await entered.Task;
        var waiting = h.Service.AcquireAsync(Harness.Request());
        fail.SetResult();

        // Assert: Both callers observe the original failure rather than a cached success.
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);

        // Arrange: Restore a healthy SDK response.
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());

        // Act & Assert: A later request succeeds, proving failures did not poison the gate or caches.
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(3, h.Sdk.Calls);
    }

    [Fact]
    public async Task Cancellation_while_waiting_does_not_cancel_owner_or_block_later_callers()
    {
        // Arrange: Hold the owner's SDK response so another caller must wait.
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };

        // Act: Start both callers and cancel only the waiter's token.
        var owner = h.Service.AcquireAsync(Harness.Request());
        using var cts = new CancellationTokenSource();
        var waiting = h.Service.AcquireAsync(Harness.Request(), cts.Token);
        cts.Cancel();

        // Assert: The canceled waiter exits without taking ownership of the gate.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        // Act: Release the owner and make a later request.
        release.SetResult();
        await owner;
        await h.Service.AcquireAsync(Harness.Request());

        // Assert: The owner stayed active and its cache fill serves the later request.
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Canceled_owner_releases_gate_and_waiter_rechecks_external_population()
    {
        // Arrange: Keep the owner's SDK acquisition pending until cancellation.
        using var h = new Harness();
        var never = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await never.Task.WaitAsync(ct);
            return h.Token();
        };
        using var cts = new CancellationTokenSource();

        // Act: Queue a waiter, populate L2 externally, and cancel the owner.
        var owner = h.Service.AcquireAsync(Harness.Request(), cts.Token);
        var waiting = h.Service.AcquireAsync(Harness.Request());
        var key = Harness.Request().CacheKey;
        await h.Store.SetAsync(key, new(key, h.Token("external"), h.Time.GetUtcNow().AddMinutes(5)), default);
        cts.Cancel();

        // Assert: The owner releases its gate and the waiter reuses the newly populated L2.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Equal("external", (await waiting.WaitAsync(TimeSpan.FromSeconds(10))).AccessToken);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task L1_works_during_L2_outage_and_best_effort_misses_fall_back()
    {
        // Arrange: Warm L1, then make both L2 reads and writes fail.
        using var h = new Harness();
        await h.Service.AcquireAsync(Harness.Request());
        h.Cache.Read = (_, _) => throw new IOException("offline");
        h.Cache.Write = (_, _, _) => throw new IOException("offline");

        // Act: Repeat the warmed request during the outage.
        await h.Service.AcquireAsync(Harness.Request());

        // Assert: The L1 hit does not require another SDK acquisition.
        Assert.Equal(1, h.Sdk.Calls);

        // Act: Request another principal whose token is absent from L1.
        await h.Service.AcquireAsync(Harness.Request(principal: "bob"));

        // Assert: Best effort falls back to the SDK despite L2 being unavailable.
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Strict_L2_policy_propagates_read_and_write_failures()
    {
        // Arrange: Disable best effort and inject an L2 read failure.
        using var h = new Harness(new() { BestEffortL2 = false });
        h.Cache.Read = (_, _) => throw new IOException("read");

        // Act & Assert: Read failure propagates before the SDK is invoked.
        await Assert.ThrowsAsync<IOException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Equal(0, h.Sdk.Calls);

        // Arrange: Restore reads but inject an L2 write failure.
        h.Cache.Read = null;
        h.Cache.Write = (_, _, _) => throw new IOException("write");

        // Act & Assert: Strict policy also rejects acquisition when persistence fails.
        await Assert.ThrowsAsync<IOException>(() => h.Service.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task L2_cancellation_is_never_swallowed()
    {
        // Arrange: Make the cache read throw cancellation with best effort enabled.
        using var h = new Harness();
        h.Cache.Read = (_, _) => throw new OperationCanceledException();

        // Act & Assert: Cancellation propagates rather than becoming SDK fallback.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Equal(0, h.Sdk.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Safety_boundary_is_strict(int secondsBeyondBuffer)
    {
        // Arrange: Position the SDK token just before, exactly at, or just after the safety cutoff.
        using var h = new Harness();
        var token = new TokenResult("boundary", h.Time.GetUtcNow() + h.Options.SafetyBuffer
            + TimeSpan.FromSeconds(secondsBeyondBuffer));
        h.Sdk.Handler = (_, _) => Task.FromResult(token);

        // Act & Assert: Only strictly positive usable lifetime is accepted.
        if (secondsBeyondBuffer <= 0)
            await Assert.ThrowsAsync<UnusableTokenException>(() => h.Service.AcquireAsync(Harness.Request()));
        else
            Assert.Equal(token, await h.Service.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task Empty_sdk_token_is_rejected_without_caching()
    {
        // Arrange: Return an empty access token with otherwise valid expiry.
        using var h = new Harness();
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token(""));

        // Act & Assert: Empty token content is rejected and never stored in L2.
        await Assert.ThrowsAsync<UnusableTokenException>(() => h.Service.AcquireAsync(Harness.Request()));
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task Token_expiring_during_L2_read_is_not_returned()
    {
        // Arrange: Advance fake time to the token's safety cutoff during the L2 read.
        using var h = new Harness();
        var token = h.Token("old", 2);
        h.Cache.Read = (key, _) =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            return Task.FromResult<CacheEnvelope?>(new(key, token, token.ExpiresAt));
        };

        // Act & Assert: Reject the delayed L2 entry and return a fresh SDK acquisition.
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Token_expiring_during_write_is_not_returned_or_promoted()
    {
        // Arrange: Make persistence consume the acquired token's entire usable lifetime.
        using var h = new Harness();
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token(minutes: 2));
        h.Cache.Write = (_, _, _) =>
        {
            h.Time.Advance(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        };

        // Act & Assert: Revalidation rejects the token after the delayed write.
        await Assert.ThrowsAsync<UnusableTokenException>(() => h.Service.AcquireAsync(Harness.Request()));

        // Arrange: Restore normal write behavior.
        h.Cache.Write = null;

        // Act: Request the token again.
        await h.Service.AcquireAsync(Harness.Request());

        // Assert: The unusable result was not promoted into a reusable L1 entry.
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task L1_and_L2_ttls_are_capped_and_promotion_does_not_reset_L2_lifetime()
    {
        // Arrange: Configure a two-minute L1 cap and four-minute L2 cap.
        using var h = new Harness(new()
        {
            SafetyBuffer = TimeSpan.FromMinutes(1),
            L1MaximumTtl = TimeSpan.FromMinutes(2),
            L2MaximumTtl = TimeSpan.FromMinutes(4)
        });

        // Act: Fill both caches through the SDK.
        await h.Service.AcquireAsync(Harness.Request());
        var key = Harness.Request().CacheKey;

        // Assert: L2's stored deadline respects its four-minute cap.
        Assert.Equal(h.Time.GetUtcNow().AddMinutes(4), (await h.Store.GetAsync(key, default))!.CacheUntil);

        // Act: Expire L1 and promote the still-usable L2 entry.
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.Service.AcquireAsync(Harness.Request());

        // Assert: Promotion avoids a second SDK acquisition.
        Assert.Equal(1, h.Sdk.Calls);

        // Act: Reach the original L2 deadline and request again.
        h.Time.Advance(TimeSpan.FromMinutes(2));
        await h.Service.AcquireAsync(Harness.Request());

        // Assert: Promotion did not extend L2 residence, so a fresh acquisition is required.
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Near_expiry_L2_promotion_recalculates_usable_lifetime()
    {
        // Arrange: Seed L2 with a token whose safety cutoff is one minute away.
        using var h = new Harness();
        var key = Harness.Request().CacheKey;
        var token = h.Token("remote", 2);
        await h.Store.SetAsync(key, new(key, token, token.ExpiresAt), default);

        // Act: Promote into L1, then advance to the safety cutoff.
        await h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromMinutes(1));

        // Act & Assert: The promoted token cannot outlive its usable lifetime.
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
    }

    [Fact]
    public async Task Another_users_envelope_is_rejected_even_under_the_requested_key()
    {
        // Arrange: Store Bob's envelope under Alice's lookup key to simulate a routing mistake.
        using var h = new Harness();
        var alice = Harness.Request();
        var bob = Harness.Request(principal: "bob");
        await h.Store.SetAsync(alice.CacheKey,
            new(bob.CacheKey, h.Token("bob-token"), h.Time.GetUtcNow().AddMinutes(10)), default);

        // Act & Assert: Alice receives an SDK result rather than Bob's mismatched cached token.
        Assert.Equal("test-token", (await h.Service.AcquireAsync(alice)).AccessToken);
    }

    [Fact]
    public async Task Restart_rehydrates_L1_from_shared_L2_without_sdk()
    {
        // Arrange: Warm shared L2, then create a service with empty process-local state.
        using var h = new Harness();
        await h.Service.AcquireAsync(Harness.Request());
        using var restarted = h.NewService();

        // Act: Acquire through the restarted service.
        await restarted.AcquireAsync(Harness.Request());

        // Assert: Shared L2 serves the request without a second SDK acquisition.
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Two_instances_can_duplicate_acquisition_and_older_write_can_win()
    {
        // Arrange: Share L2 between independent services and hold the older SDK response.
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

        // Act: Start the older acquisition and wait until it stalls inside the SDK.
        var lateWriter = h.Service.AcquireAsync(Harness.Request());
        await firstEntered.Task;

        // Act & Assert: The other instance independently acquires and caches the newer token.
        Assert.Equal(newer, await other.AcquireAsync(Harness.Request()));

        // Act: Let the older response complete after the newer write.
        releaseFirst.SetResult();

        // Assert: Local gates allow duplicate issuance and an older L2 overwrite without extending validity.
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
        // Arrange: Hold a short-lived SDK response while another same-key caller waits.
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token(minutes: 2);
        };

        // Act: Complete both callers, then advance to the cached token's safety cutoff and request again.
        var first = h.Service.AcquireAsync(Harness.Request());
        var waiting = h.Service.AcquireAsync(Harness.Request());
        release.SetResult();
        await Task.WhenAll(first, waiting);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        await h.Service.AcquireAsync(Harness.Request());

        // Assert: The now-unusable local token requires another SDK acquisition.
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public void Token_diagnostics_redact_the_secret()
    {
        // Arrange: Use a recognizable secret so accidental diagnostic exposure is detectable.
        // Act & Assert: Formatting the token must not include its access-token value.
        Assert.DoesNotContain("sensitive-secret", new TokenResult("sensitive-secret", DateTimeOffset.MaxValue).ToString());
    }
}
