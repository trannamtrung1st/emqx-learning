namespace EmqxLearning.Shared.Services.Abstracts;

public interface IDynamicRateLimiter
{
    int Limit { get; }
    int Acquired { get; }
    int Available { get; }
    int QueueCount { get; }

    Task SetLimit(int limit, CancellationToken cancellationToken = default);
    Task Acquire(CancellationToken cancellationToken = default);
    Task Release(CancellationToken cancellationToken = default);
}