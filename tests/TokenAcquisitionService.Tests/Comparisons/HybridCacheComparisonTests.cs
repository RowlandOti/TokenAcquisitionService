using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TokenAcquisitionService.Tests;

public sealed class HybridCacheComparisonTests
{
    private static ServiceProvider Provider(IDistributedCache? l2 = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (l2 is not null) services.AddSingleton(l2);
        services.AddHybridCache();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task HybridCache_coalesces_factory_calls_within_one_instance()
    {
        using var provider = Provider();
        var cache = provider.GetRequiredService<HybridCache>();
        var release = Harness.Signal();
        var count = 0;
        async ValueTask<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref count);
            await release.Task.WaitAsync(ct);
            return "value";
        }
        var calls = Enumerable.Range(0, 100).Select(_ => cache.GetOrCreateAsync("key", Factory).AsTask()).ToArray();
        release.SetResult();
        Assert.All(await Task.WhenAll(calls), value => Assert.Equal("value", value));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task HybridCache_does_not_coalesce_across_instances_even_with_shared_L2()
    {
        IDistributedCache l2 = new SharedL2();
        using var first = Provider(l2);
        using var second = Provider(l2);
        var release = Harness.Signal();
        var count = 0;
        async ValueTask<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref count);
            await release.Task.WaitAsync(ct);
            return "value";
        }
        var a = first.GetRequiredService<HybridCache>().GetOrCreateAsync("key", Factory).AsTask();
        var b = second.GetRequiredService<HybridCache>().GetOrCreateAsync("key", Factory).AsTask();
        Assert.Equal(2, count);
        release.SetResult();
        await Task.WhenAll(a, b);
    }

    [Fact]
    public async Task HybridCache_reuses_serialized_L2_in_a_new_instance()
    {
        IDistributedCache l2 = new SharedL2();
        using var first = Provider(l2);
        using var second = Provider(l2);
        var token = new TokenResult("test-only-value", DateTimeOffset.UtcNow.AddHours(1));
        await first.GetRequiredService<HybridCache>().SetAsync("token", token);
        var actual = await second.GetRequiredService<HybridCache>().GetOrCreateAsync<TokenResult>(
            "token", _ => throw new InvalidOperationException("must read L2"));
        Assert.Equal(token, actual);
    }

    [Fact]
    public async Task HybridCache_one_canceled_waiter_does_not_cancel_other_waiter()
    {
        using var provider = Provider();
        var cache = provider.GetRequiredService<HybridCache>();
        var entered = Harness.Signal();
        var release = Harness.Signal();
        var count = 0;
        async ValueTask<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref count);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return "value";
        }
        using var cts = new CancellationTokenSource();
        var first = cache.GetOrCreateAsync("key", Factory, cancellationToken: cts.Token).AsTask();
        await entered.Task;
        var second = cache.GetOrCreateAsync("key", Factory).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal("value", await second);
        Assert.Equal(1, count);
    }

    // HybridCache intentionally omits an exact MemoryDistributedCache as a redundant local backend.
    // This distinct shared adapter exercises the actual L2/serialization path between two providers.
    private sealed class SharedL2 : IDistributedCache
    {
        private readonly MemoryDistributedCache _inner = new(Options.Create(new MemoryDistributedCacheOptions()));
        public byte[]? Get(string key) => _inner.Get(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => _inner.GetAsync(key, token);
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => _inner.Set(key, value, options);
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) =>
            _inner.SetAsync(key, value, options, token);
        public void Refresh(string key) => _inner.Refresh(key);
        public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
        public void Remove(string key) => _inner.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
    }
}
