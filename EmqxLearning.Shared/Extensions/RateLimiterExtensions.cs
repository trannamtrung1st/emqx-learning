using System.Threading.RateLimiting;

namespace EmqxLearning.Shared.Extensions;

public static class RateLimiterExtensions
{
    public static async Task<RateLimitLease> WaitToAcquire(this RateLimiter rateLimiter, CancellationToken cancellationToken)
    {
        RateLimitLease lease;
        lease = await rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);
        while (!lease.IsAcquired)
        {
            lease.Dispose();
            lease = await rateLimiter.AcquireAsync(permitCount: 0, cancellationToken);
            lease.Dispose();
            lease = await rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);
        }
        return lease;
    }
}