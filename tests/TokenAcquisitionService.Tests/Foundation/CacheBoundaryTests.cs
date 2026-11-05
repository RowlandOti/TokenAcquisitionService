using Microsoft.Extensions.Logging.Abstractions;

namespace TokenAcquisitionService.Tests;

public sealed class CacheBoundaryTests
{
    [Fact]
    public async Task Matching_envelope_is_stored_and_retrieved()
    {
        // Arrange: Bind an envelope to the same canonical key used for storage.
        using var h = new Harness();
        var key = Harness.Request().CacheKey;
        var envelope = new CacheEnvelope(key, h.Token(), h.Time.GetUtcNow().AddMinutes(5));

        // Act: Persist the matching envelope.
        await h.Store.SetAsync(key, envelope, default);

        // Assert: The stored binding and token are preserved.
        Assert.Equal(envelope, await h.Store.GetAsync(key, default));
    }

    [Fact]
    public async Task Mismatched_envelope_is_rejected_without_replacing_existing_entry()
    {
        // Arrange: Seed a valid entry, then prepare another principal's envelope.
        using var h = new Harness();
        var key = Harness.Request().CacheKey;
        var original = new CacheEnvelope(key, h.Token("original"), h.Time.GetUtcNow().AddMinutes(5));
        await h.Store.SetAsync(key, original, default);
        var mismatch = original with { CacheKey = Harness.Request(principal: "bob").CacheKey };

        // Act & Assert: A routing mismatch is an explicit argument failure before storage mutation.
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => h.Store.SetAsync(key, mismatch, default));
        Assert.Equal("envelope", exception.ParamName);
        Assert.Equal(original, await h.Store.GetAsync(key, default));
    }

    [Fact]
    public async Task Canceled_mismatched_write_preserves_cancellation_and_existing_entry()
    {
        // Arrange: Supply an invalid binding with a token canceled before the write.
        using var h = new Harness();
        var key = Harness.Request().CacheKey;
        var mismatch = new CacheEnvelope("wrong-key", h.Token(), h.Time.GetUtcNow().AddMinutes(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert: Cancellation takes precedence and nothing is persisted.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.SetAsync(key, mismatch, cts.Token));
        Assert.Null(await h.Store.GetAsync(key, default));
    }

    [Fact]
    public async Task L1_token_crossing_safety_boundary_before_final_validation_is_rejected()
    {
        // Arrange: Warm L1 with a token whose cutoff can be crossed between two clock samples.
        using var h = new Harness();
        var clock = new BoundaryClock(h.Time.GetUtcNow());
        var token = new TokenResult("boundary", clock.Now.AddMinutes(2));
        h.Sdk.Handler = (_, _) => Task.FromResult(token);
        using var service = new TokenService(h.Sdk, h.Cache, clock, h.Options, NullLogger<TokenService>.Instance);
        await service.AcquireAsync(Harness.Request());
        clock.Now = token.ExpiresAt - h.Options.SafetyBuffer - TimeSpan.FromTicks(1);
        clock.AdvanceOnSecondRead = true;

        // Act & Assert: Lookup succeeds at its sample, but final validation rejects the cutoff token.
        await Assert.ThrowsAsync<UnusableTokenException>(() => service.AcquireAsync(Harness.Request()));
        Assert.Equal(2, clock.Reads);
        Assert.Equal(1, h.Sdk.Calls);
    }

    private sealed class BoundaryClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        private bool _advance;
        public int Reads { get; private set; }
        public bool AdvanceOnSecondRead
        {
            set { _advance = value; Reads = 0; }
        }
        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            if (_advance && Reads == 2) Now += TimeSpan.FromTicks(1);
            return Now;
        }
    }
}
