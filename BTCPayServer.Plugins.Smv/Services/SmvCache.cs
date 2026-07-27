using Microsoft.Extensions.Caching.Memory;

namespace BTCPayServer.Plugins.Smv.Services;

/// <summary>
/// Thin TTL cache wrapper around <see cref="IMemoryCache"/>.
/// Populated in A2; exposed in A1 so DI wiring stays honest.
/// </summary>
public class SmvCache
{
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    public T? Get<T>(string key) => _cache.TryGetValue<T>(key, out var v) ? v : default;

    public void Set<T>(string key, T value, TimeSpan ttl) =>
        _cache.Set(key, value, ttl);
}
