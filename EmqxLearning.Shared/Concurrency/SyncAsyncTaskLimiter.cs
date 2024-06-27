using EmqxLearning.Shared.Concurrency.Abstracts;
using EmqxLearning.Shared.Concurrency.Configurations;
using Microsoft.Extensions.Logging;

namespace EmqxLearning.Shared.Concurrency;

public class SyncAsyncTaskLimiter : DynamicRateLimiter, ISyncAsyncTaskLimiter
{
    private int _asyncCount;
    private readonly int _maxAsyncLimit;

    public SyncAsyncTaskLimiter(
        TaskLimiterOptions limiterOptions,
        ILogger<SyncAsyncTaskLimiter> logger) : base(limiterOptions: limiterOptions)
    {
        // Reference: https://engineering.zalando.com/posts/2019/04/how-to-set-an-ideal-thread-pool-size.html
        _maxAsyncLimit = (int)(limiterOptions.AvailableCores * limiterOptions.TargetCpuUtil * (1 + limiterOptions.WaitTime / limiterOptions.ServiceTime));
        logger.LogDebug("Max async limit: {Limit}", _maxAsyncLimit);
    }

    protected override async Task<IAsyncDisposable> AcquireCore(int count, bool wait)
    {
        var disposable = await base.AcquireCore(count, wait);
        if (disposable != null)
            Interlocked.Increment(ref _asyncCount);
        return disposable;
    }

    protected override Task Release(int count)
    {
        Interlocked.Decrement(ref _asyncCount);
        return base.Release(count);
    }

    protected override bool CanAcquired() => _asyncCount < _maxAsyncLimit && base.CanAcquired();
}