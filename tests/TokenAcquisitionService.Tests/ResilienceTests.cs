using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace TokenAcquisitionService.Tests;

public sealed class ResilienceTests
{
    [Fact]
    public async Task Shared_flights_are_reclaimed_after_success_failure_and_cancellation()
    {
        using var h = new Harness();
        for (var i = 0; i < 500; i++)
            await h.Service.AcquireAsync(Harness.Request(principal: $"user-{i}"));
        Assert.Equal(0, h.Service.ActiveFlightCount);
        h.Sdk.Handler = (_, _) => throw new InvalidOperationException();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.AcquireAsync(Harness.Request(principal: "failed")));
        Assert.Equal(0, h.Service.ActiveFlightCount);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.AcquireAsync(Harness.Request(), cts.Token));
        Assert.Equal(0, h.Service.ActiveFlightCount);
    }

    [Fact]
    public async Task Sdk_concurrency_and_pending_key_capacity_are_bounded()
    {
        using var h = new Harness(resilience: new() { MaximumConcurrentSdkCalls = 2, MaximumPendingKeys = 3 });
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var tasks = Enumerable.Range(0, 3).Select(i => h.Service.AcquireAsync(Harness.Request(principal: $"{i}"))).ToArray();
        Assert.Equal(2, h.Sdk.Calls);
        await Assert.ThrowsAsync<AcquisitionCapacityException>(() => h.Service.AcquireAsync(Harness.Request(principal: "overflow")));
        release.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, h.Sdk.Calls);
        Assert.Equal(0, h.Service.ActiveFlightCount);
    }

    [Fact]
    public async Task Acquisition_timeout_releases_flight_and_allows_recovery()
    {
        using var h = new Harness(resilience: new() { AcquisitionTimeout = TimeSpan.FromSeconds(5) });
        var never = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await never.Task.WaitAsync(ct);
            return h.Token();
        };
        var call = h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.Equal(0, h.Service.ActiveFlightCount);
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());
        await h.Service.AcquireAsync(Harness.Request());
    }

    [Fact]
    public async Task Uncooperative_sdk_keeps_physical_concurrency_slot_after_timeout()
    {
        using var h = new Harness(resilience: new()
        {
            MaximumConcurrentSdkCalls = 1,
            AcquisitionTimeout = TimeSpan.FromSeconds(5)
        });
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, _) =>
        {
            await release.Task;
            return h.Token();
        };
        var first = h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<TimeoutException>(() => first);
        var second = h.Service.AcquireAsync(Harness.Request(principal: "bob"));
        Assert.Equal(1, h.Sdk.Calls);
        release.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task L2_timeout_falls_back_then_recovers()
    {
        using var h = new Harness(resilience: new() { L2Timeout = TimeSpan.FromSeconds(2) });
        var never = Harness.Signal();
        h.Cache.Read = async (_, ct) =>
        {
            await never.Task.WaitAsync(ct);
            return null;
        };
        var call = h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(2));
        await call.WaitAsync(TimeSpan.FromSeconds(10));
        h.Cache.Read = null;
        using var restarted = h.NewService();
        await restarted.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Circuit_opens_skips_dependency_then_allows_one_recovery_probe()
    {
        using var h = new Harness(resilience: new()
        {
            L2FailureThreshold = 1,
            CircuitOpenDuration = TimeSpan.FromSeconds(10)
        });
        var reads = 0;
        h.Cache.Read = (_, _) =>
        {
            Interlocked.Increment(ref reads);
            throw new IOException("offline");
        };
        await h.Service.AcquireAsync(Harness.Request());
        await h.Service.AcquireAsync(Harness.Request(principal: "bob"));
        Assert.Equal(1, reads);
        h.Time.Advance(TimeSpan.FromSeconds(10));
        var probeEntered = Harness.Signal();
        var release = Harness.Signal();
        h.Cache.Read = async (_, ct) =>
        {
            Interlocked.Increment(ref reads);
            probeEntered.SetResult();
            await release.Task.WaitAsync(ct);
            return null;
        };
        var probe = h.Service.AcquireAsync(Harness.Request(principal: "carol"));
        await probeEntered.Task;
        await h.Service.AcquireAsync(Harness.Request(principal: "dave"));
        Assert.Equal(2, reads);
        release.SetResult();
        await probe;
        h.Cache.Read = (key, ct) =>
        {
            Interlocked.Increment(ref reads);
            return h.Store.GetAsync(key, ct);
        };
        await h.Service.AcquireAsync(Harness.Request(principal: "eve"));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task Classified_transient_sdk_failure_retries_but_permanent_failure_does_not()
    {
        using var h = new Harness(resilience: new() { RetryDelay = TimeSpan.Zero, MaximumSdkRetries = 1 });
        h.Sdk.Handler = (_, _) => h.Sdk.Calls == 1
            ? throw new TransientTokenException("retryable")
            : Task.FromResult(h.Token());
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(2, h.Sdk.Calls);
        h.Sdk.Handler = (_, _) => throw new UnauthorizedAccessException("permanent");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Service.AcquireAsync(Harness.Request(principal: "bob")));
        Assert.Equal(3, h.Sdk.Calls);
    }

    [Fact]
    public async Task Retries_are_bounded_and_fake_clock_drives_backoff()
    {
        using var h = new Harness(resilience: new() { RetryDelay = TimeSpan.FromSeconds(1), MaximumSdkRetries = 1 });
        h.Sdk.Handler = (_, _) => throw new TransientTokenException("still unavailable");
        var call = h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TransientTokenException>(() => call);
        Assert.Equal(2, h.Sdk.Calls);
    }

    [Fact]
    public async Task Shutdown_cancels_shared_work_and_rejects_new_calls()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        var first = h.Service.AcquireAsync(Harness.Request());
        var second = h.Service.AcquireAsync(Harness.Request());
        await h.Service.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Service.AcquireAsync(Harness.Request()));
    }

    [Fact]
    public async Task Canceling_every_waiter_still_allows_shared_cache_fill()
    {
        using var h = new Harness();
        var release = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return h.Token();
        };
        using var cts = new CancellationTokenSource();
        var calls = Enumerable.Range(0, 100).Select(_ => h.Service.AcquireAsync(Harness.Request(), cts.Token)).ToArray();
        cts.Cancel();
        foreach (var call in calls)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        release.SetResult();
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Structured_metrics_use_fixed_events_not_tokens_or_contexts()
    {
        var events = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "TokenAcquisitionService") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "event" && tag.Value is string value) events.Add(value);
        });
        listener.Start();
        using var h = new Harness();
        await h.Service.AcquireAsync(Harness.Request());
        await h.Service.AcquireAsync(Harness.Request());
        using var other = h.NewService();
        await other.AcquireAsync(Harness.Request());
        Assert.Contains("miss", events);
        Assert.Contains("sdk_acquisition", events);
        Assert.Contains("l1_hit", events);
        Assert.Contains("l2_hit", events);
        Assert.DoesNotContain("test-token", events);
        Assert.DoesNotContain("alice", events);
    }

    [Fact]
    public async Task Multiple_contention_waves_share_one_fill_per_key()
    {
        using var h = new Harness();
        for (var wave = 0; wave < 5; wave++)
        {
            var release = Harness.Signal();
            h.Sdk.Handler = async (_, ct) =>
            {
                await release.Task.WaitAsync(ct);
                return h.Token();
            };
            var calls = Enumerable.Range(0, 200).Select(i =>
                h.Service.AcquireAsync(Harness.Request(principal: $"wave-{wave}-key-{i % 10}"))).ToArray();
            Assert.Equal((wave + 1) * 10, h.Sdk.Calls);
            release.SetResult();
            await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, h.Service.ActiveFlightCount);
        }
    }
}
