namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface IDynamicRateLimiter : IDisposable
{
    (int Limit, int Acquired, int Available, int QueueCount) State { get; }

    string Name { get; }
    int InitialLimit { get; }
    Task<long> ResetLimit();
    Task<long> SetLimit(int limit);
    Task<IAsyncDisposable> Acquire(int count);
    Task<IAsyncDisposable> TryAcquire(int count);
    void GetRateStatistics(out int availableCountAvg, out int queueCountAvg);
    void CollectRate(int movingAverageRange);
}