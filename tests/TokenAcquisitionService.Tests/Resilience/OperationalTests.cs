using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace TokenAcquisitionService.Tests;

public sealed class OperationalTests
{
    [Fact]
    public async Task Write_timeout_is_best_effort_and_L1_survives()
    {
        using var h = new Harness(resilience: new() { L2WriteTimeout = TimeSpan.FromSeconds(1) });
        var entered = Harness.Signal();
        h.Cache.Write = async (_, _, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var call = h.Service.AcquireAsync(Harness.Request());
        await entered.Task;
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("test-token", (await call).AccessToken);
        await h.Service.AcquireAsync(Harness.Request());
        Assert.Equal(1, h.Sdk.Calls);
        Assert.Null(await h.Store.GetAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task Shutdown_interrupts_retry_backoff()
    {
        using var h = new Harness(resilience: new() { RetryDelay = TimeSpan.FromSeconds(1) });
        h.Sdk.Handler = (_, _) => throw new TransientTokenException("retry");
        var call = h.Service.AcquireAsync(Harness.Request());
        await h.Service.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(1, h.Sdk.Calls);
    }

    [Fact]
    public async Task Exponential_retry_backoff_respects_budget()
    {
        var clock = new BackoffClock();
        using var h = new Harness(resilience: new()
        {
            MaximumSdkRetries = 2,
            RetryDelay = TimeSpan.FromSeconds(3),
            MaximumRetryDelay = TimeSpan.FromSeconds(6)
        }, time: clock);
        h.Sdk.Handler = (_, _) => throw new TransientTokenException("retry");
        var call = h.Service.AcquireAsync(Harness.Request());
        h.Time.Advance(TimeSpan.FromSeconds(3));
        await clock.SecondBackoffRegistered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, h.Sdk.Calls);
        h.Time.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TransientTokenException>(() => call.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, h.Sdk.Calls);
    }

    private sealed class BackoffClock : FakeTimeProvider
    {
        public TaskCompletionSource SecondBackoffRegistered { get; } = Harness.Signal();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromSeconds(6)) SecondBackoffRegistered.TrySetResult();
            return timer;
        }
    }

    [Fact]
    public async Task Capacity_wait_cancellation_on_shutdown_does_not_leak_a_permit()
    {
        using var h = new Harness(resilience: new() { MaximumConcurrentSdkCalls = 1 });
        var never = Harness.Signal();
        h.Sdk.Handler = async (_, ct) =>
        {
            await never.Task.WaitAsync(ct);
            return h.Token();
        };
        var owner = h.Service.AcquireAsync(Harness.Request());
        var queued = h.Service.AcquireAsync(Harness.Request(principal: "bob"));
        Assert.Equal(1, h.Sdk.Calls);
        await h.Service.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(0, h.Service.ActiveFlightCount);
    }

    [Fact]
    public async Task Logs_never_include_sdk_exception_payload_or_access_token()
    {
        using var h = new Harness();
        var logger = new RecordingLogger();
        using var service = new TokenService(h.Sdk, h.Cache, h.Time, h.Options, logger);
        h.Cache.Read = (_, _) => throw new IOException("SECRET-CACHE-PAYLOAD");
        h.Cache.Write = (_, _, _) => throw new IOException("SECRET-WRITE-PAYLOAD");
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("SECRET-ACCESS-TOKEN"));
        await service.AcquireAsync(Harness.Request());
        h.Sdk.Handler = (_, _) => throw new UnauthorizedAccessException("SECRET-SDK-PAYLOAD");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AcquireAsync(Harness.Request(principal: "bob")));
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("SECRET", message));
    }

    [Fact]
    public async Task Repeated_failure_and_recovery_does_not_retain_flights()
    {
        using var h = new Harness(resilience: new() { L2FailureThreshold = 1 });
        for (var i = 0; i < 10; i++)
        {
            h.Cache.Read = (_, _) => throw new IOException("offline");
            h.Sdk.Handler = (_, _) => throw new InvalidOperationException("issuer");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                h.Service.AcquireAsync(Harness.Request(principal: $"failed-{i}")));
            h.Cache.Read = null;
            h.Time.Advance(TimeSpan.FromSeconds(15));
            h.Sdk.Handler = (_, _) => Task.FromResult(h.Token());
            await h.Service.AcquireAsync(Harness.Request(principal: $"recovered-{i}"));
            Assert.Equal(0, h.Service.ActiveFlightCount);
        }
    }

    private sealed class RecordingLogger : ILogger<TokenService>
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
