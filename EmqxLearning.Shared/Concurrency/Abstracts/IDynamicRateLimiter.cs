namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface IDynamicRateLimiter : IDisposable
{
    (int Limit, int Acquired, int Available, int QueueCount) State { get; }

    string Name { get; }
    int InitialLimit { get; }
    long ResetLimit();
    long SetLimit(int limit);
    IDisposable Acquire(int count);
    IDisposable TryAcquire(int count);
    void GetRateStatistics(out int availableCountAvg, out int queueCountAvg);
    void CollectRate(int movingAverageRange);
}