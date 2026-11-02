using System.Threading.RateLimiting;
using GameOfLife.Core;
using Microsoft.Extensions.Options;

namespace GameOfLife.Api;

/// <summary>
/// Limits how many boards each client receives per second through playback, counted in boards rather
/// than requests, so one batch of 90 costs the same as 90 single steps.
/// </summary>
/// <remarks>
/// Each client IP address gets a token bucket that refills at <see cref="GameOfLifeOptions.MaxBoardsPerSecond"/>
/// and holds up to twice <see cref="GameOfLifeOptions.MaxBatchSize"/>. That is the most the browser UI
/// ever buffers ahead, so the UI at its top speed never hits the limit, while a script asking for more
/// than that rate is slowed to it. Buckets for clients that go quiet are dropped automatically.
/// Behind a reverse proxy every client shares the proxy's address, so the proxy would need to forward
/// the client address for the limit to be per client.
/// </remarks>
public sealed class PlaybackRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly int _maxBoardsPerSecond;

    public PlaybackRateLimiter(IOptions<GameOfLifeOptions> options)
    {
        var settings = options.Value;
        _maxBoardsPerSecond = settings.MaxBoardsPerSecond;
        var bucket = new TokenBucketRateLimiterOptions
        {
            TokenLimit = 2 * settings.MaxBatchSize,
            TokensPerPeriod = settings.MaxBoardsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        };
        _limiter = PartitionedRateLimiter.Create<string, string>(
            client => RateLimitPartition.GetTokenBucketLimiter(client, _ => bucket));
    }

    /// <summary>Takes <paramref name="boards"/> from the client's allowance, or throws if there are not enough.</summary>
    /// <exception cref="PlaybackRateLimitedException">The client has used up its allowance for now.</exception>
    public void Acquire(HttpContext context, int boards)
    {
        string client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        using var lease = _limiter.AttemptAcquire(client, boards);
        if (!lease.IsAcquired)
        {
            int retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan wait)
                ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds))
                : 1;
            throw new PlaybackRateLimitedException(_maxBoardsPerSecond, retryAfter);
        }
    }

    public void Dispose() => _limiter.Dispose();
}

/// <summary>A client asked for boards faster than the playback limit allows.</summary>
public sealed class PlaybackRateLimitedException(int maxBoardsPerSecond, int retryAfterSeconds)
    : Exception($"Playback is limited to {maxBoardsPerSecond} boards per second.")
{
    public int MaxBoardsPerSecond { get; } = maxBoardsPerSecond;

    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
