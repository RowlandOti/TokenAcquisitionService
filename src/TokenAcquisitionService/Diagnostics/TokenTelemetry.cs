using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace TokenAcquisitionService;

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
