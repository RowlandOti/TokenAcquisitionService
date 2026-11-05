namespace TokenAcquisitionService.Tests;

public sealed class DistributedTests
{
    [Fact]
    public async Task Two_instances_elect_leader_and_follower_rechecks_L2()
    {
        using var h = new DistributedHarness();
        var first = h.NewService();
        var second = h.NewService();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var leader = first.AcquireAsync(Harness.Request());
        var follower = second.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
        Assert.False(follower.IsCompleted);
        release.SetResult();
        await leader;
        h.Time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal("distributed-token", (await follower.WaitAsync(TimeSpan.FromSeconds(10))).AccessToken);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Expired_leader_cannot_overwrite_newer_fence_or_release_replacement_lease()
    {
        using var h = new DistributedHarness();
        var options = new DistributedOptions { LeaseDuration = TimeSpan.FromSeconds(1) };
        var first = h.NewService(options);
        var second = h.NewService(options);
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            if (h.Sdk.Calls == 1)
            {
                await release.Task.WaitAsync(ct);
                return h.Token("late-old");
            }
            return h.Token("winner");
        };
        var stalled = first.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("winner", (await second.AcquireAsync(Harness.Request())).AccessToken);
        release.SetResult();
        Assert.Equal("winner", (await stalled).AccessToken);
        Assert.Equal(2, h.Sdk.Calls);
        Assert.Equal("winner", (await h.Store.ReadAsync(Harness.Request().CacheKey, default))!.Token.AccessToken);
    }

    [Fact]
    public async Task Failed_leader_releases_lease_and_another_instance_can_recover()
    {
        using var h = new DistributedHarness();
        var first = h.NewService();
        var second = h.NewService();
        h.Sdk.Handler = (_, _) => throw new UnauthorizedAccessException("permanent");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => first.AcquireAsync(Harness.Request()));
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());
        await second.AcquireAsync(Harness.Request());
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Atomic_conditional_writes_reject_out_of_order_generations_not_expiration_order()
    {
        using var h = new DistributedHarness();
        const string key = "conditional";
        var oldFence = await h.Store.ReserveFenceAsync(key, default);
        var newFence = await h.Store.ReserveFenceAsync(key, default);
        var newer = h.Envelope(key, 1, newFence, "authoritative") with
        {
            Token = new("authoritative", h.Time.GetUtcNow().AddMinutes(10))
        };
        Assert.True(await h.Store.TryWriteAsync(key, newer, null, default));
        Assert.False(await h.Store.TryWriteAsync(key, h.Envelope(key, 1, oldFence, "longer-expiry"), null, default));
        Assert.Equal("authoritative", (await h.Store.ReadAsync(key, default))!.Token.AccessToken);
    }

    [Fact]
    public async Task Concurrent_fence_allocation_and_writes_are_atomic()
    {
        using var h = new DistributedHarness();
        const string key = "parallel";
        var fences = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            h.Store.ReserveFenceAsync(key, default))));
        Assert.Equal(100, fences.Distinct().Count());
        var outcomes = await Task.WhenAll(fences.Select(fence => Task.Run(() =>
            h.Store.TryWriteAsync(key, h.Envelope(key, 1, fence), null, default))));
        Assert.Equal(1, outcomes.Count(result => result));
        Assert.Equal(fences.Max(), (await h.Store.ReadAsync(key, default))!.Fence);
    }

    [Fact]
    public async Task Lease_release_is_ownership_safe_and_expiry_fences_even_before_successor_writes()
    {
        using var h = new DistributedHarness();
        const string key = "lease";
        var old = (await h.Store.TryAcquireAsync(key, TimeSpan.FromSeconds(1), default))!;
        Assert.Null(await h.Store.TryAcquireAsync(key, TimeSpan.FromSeconds(1), default));
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await h.Store.TryWriteAsync(key, h.Envelope(key, 1, old.Fence), old, default));
        Assert.False(await h.Store.TryWriteAsync(key, h.Envelope(key, 1, old.Fence), null, default));
        var current = (await h.Store.TryAcquireAsync(key, TimeSpan.FromSeconds(1), default))!;
        Assert.True(current.Fence > old.Fence);
        Assert.False(await h.Store.ReleaseAsync(old, default));
        Assert.Null(await h.Store.TryAcquireAsync(key, TimeSpan.FromSeconds(1), default));
        Assert.True(await h.Store.ReleaseAsync(current, default));
    }

    [Fact]
    public async Task Invalidation_generations_advance_atomically_under_concurrency()
    {
        using var h = new DistributedHarness();
        var results = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
            h.Store.InvalidateAsync("versions", default))));
        Assert.Equal(100, results.Distinct().Count());
        Assert.Equal(101, await h.Store.ReadVersionAsync("versions", default));
    }

    [Fact]
    public async Task Generation_five_fill_cannot_become_authoritative_after_generation_six_invalidation()
    {
        using var h = new DistributedHarness();
        var key = Harness.Request().CacheKey;
        for (var i = 0; i < 4; i++) await h.Store.InvalidateAsync(key, default);
        Assert.Equal(5, await h.Store.ReadVersionAsync(key, default));
        var first = h.NewService();
        var second = h.NewService();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            if (h.Sdk.Calls == 1)
            {
                await release.Task.WaitAsync(ct);
                return h.Token("generation-five");
            }
            return h.Token("generation-six");
        };
        var staleFill = first.AcquireAsync(Harness.Request());
        await second.InvalidateAsync(Harness.Request());
        Assert.Equal(6, await h.Store.ReadVersionAsync(key, default));
        await second.AcquireAsync(Harness.Request());
        release.SetResult();
        Assert.Equal("generation-six", (await staleFill).AccessToken);
        var entry = await h.Store.ReadAsync(key, default);
        Assert.Equal(6, entry!.Generation);
        Assert.Equal("generation-six", entry.Token.AccessToken);
    }

    [Fact]
    public async Task Version_mismatch_rejects_stale_L1_and_rehydrates_new_generation()
    {
        using var h = new DistributedHarness();
        var first = h.NewService();
        var second = h.NewService();
        await first.AcquireAsync(Harness.Request());
        await second.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("new"));
        Assert.Equal("new", (await first.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Version_authority_failure_is_fail_closed_even_with_fresh_L1()
    {
        using var h = new DistributedHarness();
        var service = h.NewService();
        await service.AcquireAsync(Harness.Request());
        h.Faults.VersionRead = (_, _) => throw new IOException("offline");
        await Assert.ThrowsAsync<DistributedDependencyException>(() => service.AcquireAsync(Harness.Request()));
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Availability_policy_uses_fresh_uncached_sdk_instead_of_trusting_stale_metadata()
    {
        using var h = new DistributedHarness();
        var service = h.NewService(new() { FailurePolicy = DistributedFailurePolicy.FreshSdkWithoutCaching });
        await service.AcquireAsync(Harness.Request());
        h.Faults.VersionRead = (_, _) => throw new IOException("offline");
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("uncached"));
        Assert.Equal("uncached", (await service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal("uncached", (await service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(3, h.Sdk.Calls);
        Assert.Equal("distributed-token", (await h.Store.ReadAsync(Harness.Request().CacheKey, default))!.Token.AccessToken);
    }

    [Fact]
    public async Task Missing_version_metadata_is_not_treated_as_a_valid_default()
    {
        using var h = new DistributedHarness();
        var service = h.NewService();
        await service.AcquireAsync(Harness.Request());
        h.Faults.VersionRead = (_, _) => Task.FromResult<long?>(null);
        var error = await Assert.ThrowsAsync<DistributedDependencyException>(() => service.AcquireAsync(Harness.Request()));
        Assert.IsType<MissingVersionException>(error.InnerException);
    }

    [Fact]
    public async Task Local_version_lease_allows_documented_staleness_then_recovers()
    {
        using var h = new DistributedHarness();
        var first = h.NewService(new() { LocalVersionLease = TimeSpan.FromSeconds(10) });
        var second = h.NewService();
        await first.AcquireAsync(Harness.Request());
        var reads = h.Faults.VersionReads;
        await first.AcquireAsync(Harness.Request());
        Assert.Equal(reads, h.Faults.VersionReads);
        await second.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("new"));
        await second.AcquireAsync(Harness.Request());
        Assert.Equal("distributed-token", (await first.AcquireAsync(Harness.Request())).AccessToken);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("new", (await first.AcquireAsync(Harness.Request())).AccessToken);
    }

    [Fact]
    public async Task Valid_local_version_lease_survives_authority_outage_but_expired_lease_does_not()
    {
        using var h = new DistributedHarness();
        var service = h.NewService(new() { LocalVersionLease = TimeSpan.FromSeconds(10) });
        await service.AcquireAsync(Harness.Request());
        h.Faults.VersionRead = (_, _) => throw new IOException("offline");
        await service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<DistributedDependencyException>(() => service.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task Versions_can_be_disabled_without_forcing_every_L1_hit_to_query_authority()
    {
        using var h = new DistributedHarness();
        var service = h.NewService(new() { EnableLeases = false, ValidateVersions = false });
        await service.AcquireAsync(Harness.Request());
        var reads = h.Faults.VersionReads;
        h.Faults.VersionRead = (_, _) => throw new IOException("offline");
        await service.AcquireAsync(Harness.Request());
        Assert.Equal(reads, h.Faults.VersionReads);
    }

    [Fact]
    public async Task Conditional_write_failure_is_explicit_and_never_populates_L1()
    {
        using var h = new DistributedHarness();
        var service = h.NewService();
        h.Faults.Write = (_, _, _, _) => throw new IOException("conditional failure");
        await Assert.ThrowsAsync<DistributedDependencyException>(() => service.AcquireAsync(Harness.Request()));
        h.Faults.Write = null;
        await service.AcquireAsync(Harness.Request());
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Distributed_restart_reuses_shared_versioned_L2()
    {
        using var h = new DistributedHarness();
        await h.NewService().AcquireAsync(Harness.Request());
        await h.NewService().AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_during_coordination_does_not_cancel_other_waiters()
    {
        using var h = new DistributedHarness();
        var first = h.NewService();
        var second = h.NewService();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var leader = first.AcquireAsync(Harness.Request());
        using var cts = new CancellationTokenSource();
        var abandoned = second.AcquireAsync(Harness.Request(), cts.Token);
        var waiting = second.AcquireAsync(Harness.Request());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        release.SetResult();
        await leader;
        h.Time.Advance(TimeSpan.FromMilliseconds(50));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Multi_instance_parallel_tenants_never_share_tokens()
    {
        using var h = new DistributedHarness();
        var services = Enumerable.Range(0, 5).Select(_ => h.NewService()).ToArray();
        var release = Harness.Signal();
        h.Sdk.Handler = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token(request.Tenant);
        };
        var calls = Enumerable.Range(0, 100).Select(i =>
            services[i % 5].AcquireAsync(Harness.Request(tenant: $"tenant-{i}"))).ToArray();
        Assert.Equal(100, h.Sdk.Calls);
        release.SetResult();
        var tokens = await Task.WhenAll(calls);
        for (var i = 0; i < tokens.Length; i++) Assert.Equal($"tenant-{i}", tokens[i].AccessToken);
    }

    [Fact]
    public async Task Authority_timeout_is_bounded_and_can_recover()
    {
        using var h = new DistributedHarness();
        var service = h.NewService();
        h.Faults.VersionRead = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        };
        var call = service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<DistributedDependencyException>(() => call);
        h.Faults.VersionRead = null;
        await service.AcquireAsync(Harness.Request());
    }

    [Fact]
    public async Task Shutdown_cancels_version_lookup_before_a_shared_flight_exists()
    {
        using var h = new DistributedHarness();
        var service = h.NewService();
        h.Faults.VersionRead = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return 1;
        };
        var call = service.AcquireAsync(Harness.Request());
        Assert.Equal(0, service.ActiveFlightCount);
        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(0, h.Sdk.Calls);
    }
}
