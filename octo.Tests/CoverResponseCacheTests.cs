using Octo.Services.CoverArt;

namespace Octo.Tests;

/// <summary>
/// The cache that keeps already-composed cover responses: badged external art keyed by
/// id and who is asking (the Octo app gets covers plain), and relayed library art keyed
/// by id and size, with the content type Navidrome answered under. What a repeat must
/// not do is repeat the compose.
/// </summary>
public sealed class CoverResponseCacheTests
{
    [Fact]
    public void ExternalCover_RoundTripsPerIdAndPlain()
    {
        var cache = new CoverResponseCache();
        cache.RememberExternalCover("id1", plain: false, [1, 2, 3]);

        Assert.Equal([1, 2, 3], cache.ExternalCover("id1", plain: false));
        // The Octo app variant is a different entry: badge and no-badge must not share.
        Assert.Null(cache.ExternalCover("id1", plain: true));
        Assert.Null(cache.ExternalCover("id2", plain: false));
    }

    [Fact]
    public void RelayedCover_RoundTripsPerIdAndSize_WithContentType()
    {
        var cache = new CoverResponseCache();
        cache.RememberRelayedCover("mf-1", 800, [9, 8, 7], "image/png");

        var hit = cache.RelayedCover("mf-1", 800);
        Assert.Equal([9, 8, 7], hit?.Bytes);
        // Navidrome serves PNG library covers too; the label rides with the bytes.
        Assert.Equal("image/png", hit?.ContentType);
        // Navidrome sizes to the request, so another size is another response.
        Assert.Null(cache.RelayedCover("mf-1", 300));
        Assert.Null(cache.RelayedCover("mf-2", 800));
    }

    [Fact]
    public void Set_RejectsEmptyBodies()
    {
        var cache = new CoverResponseCache();
        cache.RememberExternalCover("id1", plain: false, []);
        Assert.Null(cache.ExternalCover("id1", plain: false));
    }

    [Fact]
    public void Cache_HoldsRealisticCoverSizes()
    {
        // A cover is tens of kilobytes, not 16 bytes: what the byte budget really holds.
        var cache = new CoverResponseCache(256 * 1024);
        cache.RememberExternalCover("mf-1", plain: false, new byte[50 * 1024]);

        // 40 KB more: two 50 KB entries fit a 256 KB budget — the entry limit is
        // bytes, so the second must survive, not be evicted by a count of sizes.
        cache.RememberExternalCover("mf-2", plain: false, new byte[40 * 1024]);
        Assert.Equal(new byte[50 * 1024], cache.ExternalCover("mf-1", plain: false));
        Assert.Equal(new byte[40 * 1024], cache.ExternalCover("mf-2", plain: false));
    }

    [Fact]
    public void Set_RejectsABodyLargerThanTheWholeCache()
    {
        var cache = new CoverResponseCache(256 * 1024);
        cache.RememberExternalCover("kept", plain: false, new byte[50 * 1024]);
        cache.RememberExternalCover("huge", plain: false, new byte[300 * 1024]);

        Assert.Null(cache.ExternalCover("huge", plain: false));
        Assert.Equal(new byte[50 * 1024], cache.ExternalCover("kept", plain: false));
    }

    [Fact]
    public void Cache_TrimsWhenTheBudgetWouldBeExceeded()
    {
        // Probed MemoryCache behavior (net9.0): an entry that would push the cache over
        // its SizeLimit compacts synchronously at Set — least-recent entries go, the
        // cache may overshoot the trim (drop more than needed, even the arriving one).
        // The contract for this cache is the byte budget with eventual trimming, not a
        // named-survivor rule, so the assert is only that the total stays within the
        // budget's worth of full entries: at most two 4096-entry cache slots at 8192.
        var cache = new CoverResponseCache(2 * 4096);
        cache.RememberExternalCover("mf-0", plain: false, new byte[4096]);
        cache.RememberExternalCover("mf-1", plain: false, new byte[4096]);
        cache.RememberExternalCover("mf-2", plain: false, new byte[4096]);
        cache.RememberExternalCover("mf-3", plain: false, new byte[4096]);

        var present = new[] { "mf-0", "mf-1", "mf-2", "mf-3" }
            .Count(id => cache.ExternalCover(id, plain: false) != null);
        Assert.True(present <= 2, $"{present} entries survived a 8192-byte budget");
    }
}