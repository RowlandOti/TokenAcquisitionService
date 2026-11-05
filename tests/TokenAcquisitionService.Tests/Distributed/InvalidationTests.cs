namespace TokenAcquisitionService.Tests;

public sealed class InvalidationTests
{
    [Fact]
    public async Task Delivered_notification_evicts_L1_and_local_version_lease_immediately()
    {
        using var h = new DistributedHarness();
        var first = h.NewService(new() { LocalVersionLease = TimeSpan.FromSeconds(10) }, h.Bus);
        var second = h.NewService(bus: h.Bus);
        await first.AcquireAsync(Harness.Request());
        await second.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("new"));
        Assert.Equal("new", (await first.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Duplicate_and_out_of_order_notifications_do_not_regress_authoritative_generation()
    {
        using var h = new DistributedHarness();
        var service = h.NewService(bus: h.Bus);
        await service.AcquireAsync(Harness.Request());
        await service.InvalidateAsync(Harness.Request());
        await service.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("generation-three"));
        await service.AcquireAsync(Harness.Request());
        var key = Harness.Request().CacheKey;
        await h.Bus.PublishAsync(new(key, 3), default);
        await h.Bus.PublishAsync(new(key, 3), default);
        await h.Bus.PublishAsync(new(key, 2), default);
        Assert.Equal("generation-three", (await service.AcquireAsync(Harness.Request())).AccessToken);
        Assert.Equal(2, h.Sdk.Calls);
        Assert.Equal(3, (await h.Store.ReadAsync(key, default))!.Generation);
    }

    [Fact]
    public async Task Disconnected_still_running_instance_misses_notification_and_recovers_after_version_lease()
    {
        using var h = new DistributedHarness();
        var connection = new SwitchableBus(h.Bus);
        var first = h.NewService(new() { LocalVersionLease = TimeSpan.FromSeconds(10) }, connection);
        var second = h.NewService(bus: h.Bus);
        await first.AcquireAsync(Harness.Request());
        connection.Subscriptions.Single().Connected = false;
        await second.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("new"));
        await second.AcquireAsync(Harness.Request());
        Assert.Equal("distributed-token", (await first.AcquireAsync(Harness.Request())).AccessToken);
        connection.Subscriptions.Single().Connected = true;
        // Reconnecting does not replay; authoritative version validation repairs after lease expiry.
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal("new", (await first.AcquireAsync(Harness.Request())).AccessToken);
    }

    [Fact]
    public async Task Without_version_checks_missed_notification_recovers_only_after_L1_residence()
    {
        using var h = new DistributedHarness();
        var connection = new SwitchableBus(h.Bus);
        var first = h.NewService(new() { ValidateVersions = false }, connection);
        var second = h.NewService(bus: h.Bus);
        await first.AcquireAsync(Harness.Request());
        connection.Subscriptions.Single().Connected = false;
        await second.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("new"));
        await second.AcquireAsync(Harness.Request());
        Assert.Equal("distributed-token", (await first.AcquireAsync(Harness.Request())).AccessToken);
        h.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal("new", (await first.AcquireAsync(Harness.Request())).AccessToken);
    }

    [Fact]
    public async Task Fenced_writes_work_without_distributed_leases()
    {
        using var h = new DistributedHarness();
        var first = h.NewService(new() { EnableLeases = false });
        var second = h.NewService(new() { EnableLeases = false });
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            if (h.Sdk.Calls == 1)
            {
                await release.Task.WaitAsync(ct);
                return h.Token("late");
            }
            return h.Token("winner");
        };
        var late = first.AcquireAsync(Harness.Request());
        await second.AcquireAsync(Harness.Request());
        release.SetResult();
        Assert.Equal("winner", (await late).AccessToken);
    }

    [Fact]
    public async Task Concurrent_instances_stress_one_key_without_duplicate_healthy_acquisition()
    {
        using var h = new DistributedHarness();
        var services = Enumerable.Range(0, 5).Select(_ => h.NewService()).ToArray();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var calls = Enumerable.Range(0, 100).Select(i => services[i % 5].AcquireAsync(Harness.Request())).ToArray();
        Assert.Equal(1, h.Sdk.Calls);
        release.SetResult();
        await calls[0];
        h.Time.Advance(TimeSpan.FromMilliseconds(50));
        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, h.Sdk.Calls);
        Assert.All(services, service => Assert.Equal(0, service.ActiveFlightCount));
    }

    [Fact]
    public async Task Shutdown_cancels_follower_coordination_without_waiting_for_lease_expiry()
    {
        using var h = new DistributedHarness();
        var key = Harness.Request().CacheKey;
        var external = (await h.Store.TryAcquireAsync(key, TimeSpan.FromMinutes(1), default))!;
        var follower = h.NewService();
        var call = follower.AcquireAsync(Harness.Request());
        Assert.Equal(0, h.Sdk.Calls);
        await follower.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(await h.Store.ReleaseAsync(external, default));
    }

    [Fact]
    public async Task Delayed_old_local_fill_cannot_replace_newer_generation()
    {
        using var h = new DistributedHarness();
        var first = h.NewService();
        var releaseEntered = Harness.Signal();
        var releaseOld = Harness.Signal();
        var blocked = false;
        h.Faults.Release = async (lease, ct) =>
        {
            if (!blocked)
            {
                blocked = true;
                releaseEntered.SetResult();
                await releaseOld.Task.WaitAsync(ct);
            }
            return await h.Store.ReleaseAsync(lease, ct);
        };
        var old = first.AcquireAsync(Harness.Request());
        await releaseEntered.Task;
        await first.InvalidateAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("new-generation"));
        Assert.Equal("new-generation", (await first.AcquireAsync(Harness.Request())).AccessToken);
        releaseOld.SetResult();
        Assert.Equal("new-generation", (await old).AccessToken);
        // No L2 access should be needed to repair a regressed local fill.
        h.Faults.CacheRead = (_, _) => throw new IOException("L1 must retain the newer generation");
        Assert.Equal("new-generation", (await first.AcquireAsync(Harness.Request())).AccessToken);
    }
}
