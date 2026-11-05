namespace TokenAcquisitionService;

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
