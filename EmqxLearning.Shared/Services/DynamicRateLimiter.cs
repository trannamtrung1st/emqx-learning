using EmqxLearning.Shared.Services.Abstracts;

namespace EmqxLearning.Shared.Services;

public class DynamicRateLimiter : IDynamicRateLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private int _queueCount = 0;
    public int QueueCount => _queueCount;
    private int _limit = 0;
    public int Limit => _limit;
    private int _acquired = 0;
    public int Acquired => _acquired;
    public int Available => _limit - _acquired;

    public DynamicRateLimiter()
    {
        _semaphore = new SemaphoreSlim(1);
    }

    public async Task Acquire(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _queueCount);
        while (true)
        {
            await _semaphore.WaitAsync(cancellationToken: cancellationToken);
            try
            {
                if (_acquired < _limit)
                {
                    _acquired++;
                    Interlocked.Decrement(ref _queueCount);
                    return;
                }
            }
            finally { _semaphore.Release(); }
            await Task.Delay(50);
        }
    }

    public async Task Release(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken: cancellationToken);
        try
        {
            if (_acquired > 0)
                _acquired--;
        }
        finally { _semaphore.Release(); }
    }

    public async Task SetLimit(int limit, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken: cancellationToken);
        try
        { _limit = limit; }
        finally { _semaphore.Release(); }
    }
}