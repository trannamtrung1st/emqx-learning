namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface IConsumerRateLimiters
{
    ISyncAsyncTaskLimiter TaskLimiter { get; }
    IDynamicRateLimiter SizeLimiter { get; }
    IEnumerable<IDynamicRateLimiter> RateLimiters { get; }
}