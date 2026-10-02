using System.Threading.RateLimiting;

namespace Octo.Services.Fingerprint;

/// <summary>
/// Keeps Octo inside AcoustID's published budget of 3 requests per second.
///
/// The same trap as Deezer, and it bites harder here: over-budget is not reliably a 429,
/// it is an error envelope in a 200 body. That parses as "no match", and this feature reads
/// "no match" as "accept the file", so exceeding the budget would silently turn
/// verification OFF rather than merely slow it down.
///
/// One budget with a background lane. A download's lookup sits between a finished transfer and
/// the library and waits in the queue. The library sweep (#72) never waits: it takes a permit
/// only when one is free and no download is queued (OldestFirst refuses a non-queuing request
/// while anyone waits), so it can never take a queue slot a download needed.
/// </summary>
public sealed class AcoustIdRateLimiter : IDisposable
{
    /// <summary>Named HttpClient that carries the limiting handler. A caller that resolves
    /// any other client bypasses the budget entirely.</summary>
    public const string ClientName = "acoustid";

    private const int PermitsPerSecond = 3;

    private static readonly AsyncLocal<bool> BackgroundFlow = new();

    private readonly SlidingWindowRateLimiter _limiter = new(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = PermitsPerSecond,
        Window = TimeSpan.FromSeconds(1),
        SegmentsPerWindow = 3,
        // Must be set explicitly: it defaults to 0, which makes AcquireAsync return a
        // NON-acquired lease immediately instead of waiting. A dropped lookup here is an
        // accepted file, so dropping is the one thing this must not do by default.
        // Bounded by the client's 10s timeout: 3/s drains 12 in four seconds.
        QueueLimit = 12,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        AutoReplenishment = true,
    });

    /// <summary>Lookups started inside <paramref name="work"/> use the background lane.</summary>
    public static async Task<T> InBackgroundAsync<T>(Func<Task<T>> work)
    {
        // Set inside an async method, so the flag ends when it returns and never reaches the caller.
        BackgroundFlow.Value = true;
        return await work();
    }

    internal static bool InBackground => BackgroundFlow.Value;

    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken ct) => BackgroundFlow.Value
        ? ValueTask.FromResult(_limiter.AttemptAcquire(1))
        : _limiter.AcquireAsync(1, ct);

    public void Dispose() => _limiter.Dispose();
}
