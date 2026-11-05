using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace TokenAcquisitionService.Tests;

public sealed class DistributedObservabilityTests
{
    [Fact]
    public async Task Distributed_events_and_logs_never_include_tokens_or_dependency_exception_payloads()
    {
        using var h = new DistributedHarness();
        var logger = new RecordingLogger();
        var events = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "TokenAcquisitionService") meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Value is string value) events.Add(value);
        });
        listener.Start();
        using var service = new TokenService(h.Sdk, h.Store, h.Time, new(), logger,
            distributed: new(h.Faults, h.Faults, h.Faults, h.Bus,
                new() { FailurePolicy = DistributedFailurePolicy.FreshSdkWithoutCaching }));
        h.Sdk.Handler = (_, _) => Task.FromResult(h.Token("SECRET-ACCESS-TOKEN"));
        await service.AcquireAsync(Harness.Request());
        await service.InvalidateAsync(Harness.Request());
        h.Faults.VersionRead = (_, _) => throw new IOException("SECRET-STORE-PAYLOAD");
        await service.AcquireAsync(Harness.Request());
        Assert.Contains("lease_acquired", events);
        Assert.Contains("invalidation_sent", events);
        Assert.Contains("invalidation_received", events);
        Assert.Contains("version_store_failure", events);
        Assert.Contains("distributed_fallback", events);
        Assert.All(logger.Messages, message => Assert.DoesNotContain("SECRET", message));
        Assert.All(events, value => Assert.DoesNotContain("SECRET", value));
    }

    [Fact]
    public async Task Null_sdk_result_is_rejected_as_unusable_without_caching()
    {
        using var h = new DistributedHarness();
        h.Sdk.Handler = (_, _) => Task.FromResult<TokenResult>(null!);
        await Assert.ThrowsAsync<UnusableTokenException>(() => h.NewService().AcquireAsync(Harness.Request()));
        Assert.Null(await h.Store.ReadAsync(Harness.Request().CacheKey, default));
    }

    [Fact]
    public async Task Malformed_L2_envelope_cannot_return_a_null_token()
    {
        using var h = new Harness();
        var key = Harness.Request().CacheKey;
        await h.Store.SetAsync(key, new(key, null!, h.Time.GetUtcNow().AddMinutes(5)), default);
        Assert.Equal("test-token", (await h.Service.AcquireAsync(Harness.Request())).AccessToken);
    }

    private sealed class RecordingLogger : ILogger<TokenService>
    {
        public ConcurrentBag<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
