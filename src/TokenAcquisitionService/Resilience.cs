using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace TokenAcquisitionService;

public sealed record ResilienceOptions
{
    public int MaximumConcurrentSdkCalls { get; init; } = 128;
    public int MaximumPendingKeys { get; init; } = 4096;
    public TimeSpan AcquisitionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan L2Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan? L2ReadTimeout { get; init; }
    public TimeSpan? L2WriteTimeout { get; init; }
    public int L2FailureThreshold { get; init; } = 3;
    public TimeSpan CircuitOpenDuration { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumSdkRetries { get; init; } = 1;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumRetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    public bool UseRetryJitter { get; init; }

    internal void Validate()
    {
        if (MaximumConcurrentSdkCalls <= 0 || MaximumPendingKeys <= 0 || L2FailureThreshold <= 0
            || AcquisitionTimeout <= TimeSpan.Zero || L2Timeout <= TimeSpan.Zero
            || L2ReadTimeout <= TimeSpan.Zero || L2WriteTimeout <= TimeSpan.Zero
            || CircuitOpenDuration <= TimeSpan.Zero || MaximumSdkRetries is < 0 or > 5 || RetryDelay < TimeSpan.Zero
            || MaximumRetryDelay <= TimeSpan.Zero || RetryDelay > MaximumRetryDelay)
            throw new ArgumentOutOfRangeException(nameof(ResilienceOptions));
    }
}

/// <summary>Only SDK adapters may classify a failure as safe to retry.</summary>
public sealed class TransientTokenException(string message) : Exception(message);
public sealed class CacheCircuitOpenException() : Exception("The L2 circuit is open.");
public sealed class AcquisitionCapacityException() : Exception("Too many distinct token acquisitions are pending.");

internal sealed class TokenTelemetry : IDisposable
{
    private readonly Meter _meter = new("TokenAcquisitionService");
    private readonly Counter<long> _counter;
    private readonly Histogram<double> _latency;
    private readonly ILogger<TokenService> _logger;

    public TokenTelemetry(ILogger<TokenService> logger)
    {
        _logger = logger;
        _counter = _meter.CreateCounter<long>("token.events");
        _latency = _meter.CreateHistogram<double>("token.sdk.duration", "ms");
    }

    public void SdkDuration(TimeSpan duration) => _latency.Record(duration.TotalMilliseconds);

    public void Record(string name, bool warning = false)
    {
        // Fixed low-cardinality events only: no token, context, key, or exception payload.
        _counter.Add(1, new KeyValuePair<string, object?>("event", name));
        if (warning) _logger.LogWarning("Token acquisition event {Event}", name);
        else _logger.LogDebug("Token acquisition event {Event}", name);
    }

    public void Dispose() => _meter.Dispose();
}

internal sealed class L2Circuit(TimeProvider time, ResilienceOptions options, TokenTelemetry telemetry)
{
    private int _failures;
    private long _openUntil;
    private int _probe;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken,
        bool write = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var openUntil = Volatile.Read(ref _openUntil);
        var probe = openUntil != 0;
        if (probe && (time.GetUtcNow().UtcTicks < openUntil || Interlocked.CompareExchange(ref _probe, 1, 0) != 0))
            throw new CacheCircuitOpenException();
        if (probe) telemetry.Record("circuit_half_open");
        try
        {
            var duration = (write ? options.L2WriteTimeout : options.L2ReadTimeout) ?? options.L2Timeout;
            using var timeout = new CancellationTokenSource(duration, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            T result;
            try
            {
                var task = operation(linked.Token);
                Observe(task);
                result = await task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
            {
                telemetry.Record("l2_timeout", true);
                throw new TimeoutException("L2 operation exceeded its deadline.");
            }
            Interlocked.Exchange(ref _failures, 0);
            Volatile.Write(ref _openUntil, 0);
            if (probe) telemetry.Record("circuit_closed");
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (probe || Interlocked.Increment(ref _failures) >= options.L2FailureThreshold)
            {
                Volatile.Write(ref _openUntil, (time.GetUtcNow() + options.CircuitOpenDuration).UtcTicks);
                telemetry.Record("circuit_open", true);
            }
            throw;
        }
        finally
        {
            if (probe) Volatile.Write(ref _probe, 0);
        }
    }

    internal static void Observe(Task task) =>
        _ = task.ContinueWith(completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
