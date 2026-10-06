using Microsoft.Extensions.Caching.Memory;

namespace Octo.Services.CoverArt;

/// <summary>
/// Remembers cover responses Octo already composed, so a client that re-asks for the
/// same one — an app refreshing a queue, or re-fetching after dropping the connection —
/// does not repeat the work. Two kinds live here, and the key says which:
///
/// - badged/unbadged external art ("badge", key id, plain): the registry branch looks
///   the art up once, composites the Octo badge on it, and serves the result. The bytes
///   depend on the id and on whether the client draws its own marks ("c=Octo"), not on
///   the size the client asked at: the source art is fetched at a fixed upstream
///   resolution and sent back at source dimensions. Composing decodes the source JPEG
///   to a full RGBA32 image and re-encodes it — multi-megabyte large-object churn per
///   request on a shared host.
///
/// - relayed library art ("nav", key id, size): Navidrome's answer for a library cover,
///   which it sizes to the request, with the content type it answered under (PNG or
///   JPEG). Cached only after Navidrome has already accepted the caller's sign-in on
///   that request; the cache never answers before the credential check.
///
/// The cache owns the accounting, as the aggregator's does: the budget is bytes, and
/// every entry is charged its length, least-recently-used entries evicted at the cap.
/// A hit slides the entry's one-hour expiry back, but never past a six-hour floor: a
/// cover whose art changes underneath is corrected at worst six hours late, however
/// hot the entry is.
/// </summary>
public sealed class CoverResponseCache
{
    // 128 MiB of JPEGs is thousands of covers; half the aggregator's ceiling.
    private static readonly long DefaultMaxBytes = 128L * 1024 * 1024;

    internal long MaxBytes { get; }

    private static readonly TimeSpan SlidingLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>A composed cover: the bytes and the content type to serve them under.</summary>
    public sealed record Entry(byte[] Bytes, string ContentType);

    private readonly MemoryCache _cache;

    public CoverResponseCache() : this(DefaultMaxBytes) { }

    /// <summary>A ctor-level bound (tests shrink it): the cache is built on it once.</summary>
    internal CoverResponseCache(long maxBytes) =>
        (MaxBytes, _cache) = (maxBytes, new MemoryCache(new MemoryCacheOptions { SizeLimit = maxBytes }));

    public byte[]? ExternalCover(string id, bool plain)
    {
        var key = $"badge|{(plain ? 'p' : 'b')}|{id}";
        if (_cache.TryGetValue(key, out Entry? entry)) return entry?.Bytes;
        return null;
    }

    public void RememberExternalCover(string id, bool plain, byte[] bytes) =>
        Set($"badge|{(plain ? 'p' : 'b')}|{id}", bytes, "image/jpeg");

    public Entry? RelayedCover(string id, int? requestedSize)
    {
        var key = $"nav|{id}|{requestedSize?.ToString() ?? "-"}";
        if (_cache.TryGetValue(key, out Entry? entry)) return entry;
        return null;
    }

    public void RememberRelayedCover(string id, int? requestedSize, byte[] bytes, string contentType) =>
        Set($"nav|{id}|{requestedSize?.ToString() ?? "-"}", bytes, contentType);

    private void Set(string key, byte[] bytes, string contentType)
    {
        if (bytes.Length == 0 || bytes.Length > MaxBytes) return;
        _cache.Set(key, new Entry(bytes, contentType), new MemoryCacheEntryOptions
        {
            Size = Math.Max(bytes.Length, 1),
            SlidingExpiration = SlidingLifetime,
            AbsoluteExpirationRelativeToNow = MaxAge,
        });
    }
}