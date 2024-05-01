namespace EmqxLearning.Shared.Services.Abstracts;

public interface IDynamicRateLimiter
{
    (int Limit, int Acquired, int Available, int QueueCount) State { get; }

    Task SetLimit(int limit, CancellationToken cancellationToken = default);
    Task Acquire(CancellationToken cancellationToken = default);
    Task Release(CancellationToken cancellationToken = default);
}