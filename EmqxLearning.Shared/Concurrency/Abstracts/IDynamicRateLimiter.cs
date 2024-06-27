namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface IDynamicRateLimiter : IDisposable
{
    (long Limit, long Acquired, long Available, long QueueCount) State { get; }

    string Name { get; }
    long InitialLimit { get; }
    long ResetLimit();
    long SetLimit(long limit);
    IDisposable Acquire(long count);
    bool TryAcquire(long count, out IDisposable scope);
    void GetRateStatistics(out long availableCountAvg, out long queueCountAvg);
    void CollectRate(int movingAverageRange);
}