using Polly;
using Polly.Retry;

namespace EmqxLearning.Shared.Extensions;

public static class ResiliencePipelineBuilderExtensions
{
    public static ResiliencePipelineBuilder<TResult> AddDefaultRetry<TResult>(
        this ResiliencePipelineBuilder<TResult> builder,
        int retryAttempts = 3, int delaySecs = 3, DelayBackoffType backoffType = DelayBackoffType.Constant,
        Func<RetryPredicateArguments<TResult>, ValueTask<bool>> shouldHandle = null,
        Func<OnRetryArguments<TResult>, ValueTask> onRetry = null)
    {
        return builder.AddRetry(new RetryStrategyOptions<TResult>
        {
            BackoffType = backoffType,
            UseJitter = true,
            MaxRetryAttempts = retryAttempts,
            Delay = TimeSpan.FromSeconds(delaySecs),
            OnRetry = onRetry,
            ShouldHandle = shouldHandle ?? new PredicateBuilder<TResult>().Handle<Exception>(),
        });
    }

    public static ResiliencePipelineBuilder AddDefaultRetry(
        this ResiliencePipelineBuilder builder,
        int retryAttempts = 3, int delaySecs = 3,
        Func<RetryPredicateArguments<object>, ValueTask<bool>> shouldHandle = null,
        Func<OnRetryArguments<object>, ValueTask> onRetry = null)
    {
        return builder.AddRetry(new RetryStrategyOptions
        {
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            MaxRetryAttempts = retryAttempts,
            Delay = TimeSpan.FromSeconds(delaySecs),
            OnRetry = onRetry,
            ShouldHandle = shouldHandle ?? new PredicateBuilder().Handle<Exception>(),
        });
    }
}