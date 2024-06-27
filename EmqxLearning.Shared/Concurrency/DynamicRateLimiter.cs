using EmqxLearning.Shared.Concurrency.Abstracts;
using EmqxLearning.Shared.Concurrency.Configurations;

namespace EmqxLearning.Shared.Concurrency;

public class DynamicRateLimiter : IDynamicRateLimiter, IDisposable
{
    private readonly ManualResetEventSlim _availableEvent;
    private readonly SemaphoreSlim _lock = new(1);
    private readonly RateLimiterOptions _limiterOptions;
    private readonly Queue<long> _queueCounts = new();
    private readonly Queue<long> _availableCounts = new();
    private int _limit = 0;
    private int _queueCount = 0;
    private int _acquired = 0;

    public DynamicRateLimiter(RateLimiterOptions limiterOptions)
    {
        _availableEvent = new ManualResetEventSlim();
        _limiterOptions = limiterOptions;
        SetLimit(limit: _limiterOptions.InitialLimit).Wait();
    }

    public string Name => _limiterOptions.Name;
    public int InitialLimit => _limiterOptions.InitialLimit;
    public (int Limit, int Acquired, int Available, int QueueCount) State
    {
        get
        {
            _lock.Wait();
            try { return (_limit, _acquired, _limit - _acquired, _queueCount); }
            finally { _lock.Release(); }
        }
    }

    public Task<IAsyncDisposable> Acquire(int count)
        => AcquireCore(count, wait: true);

    public Task<IAsyncDisposable> TryAcquire(int count)
        => AcquireCore(count, wait: false);

    protected virtual async Task Release(int count)
    {
        await _lock.WaitAsync();
        try
        {
            if (_acquired > 0)
            {
                _acquired -= count;
                _availableEvent.Set();
            }
        }
        finally { _lock.Release(); }
    }

    public async Task<long> SetLimit(int limit)
    {
        var acceptedLimit = GetAcceptedLimit(limit);
        await _lock.WaitAsync();
        try
        {
            var prevLimit = _limit;
            _limit = acceptedLimit;
            if (_limit > prevLimit) _availableEvent.Set();
        }
        finally { _lock.Release(); }
        return acceptedLimit;
    }

    public Task<long> ResetLimit() => SetLimit(limit: _limiterOptions.InitialLimit);

    protected virtual async Task<IAsyncDisposable> AcquireCore(int count, bool wait)
    {
        if (_limit == 0)
            return null;
        bool queued = false;
        bool acquired = false;
        try
        {
            while (!acquired)
            {
                await _lock.WaitAsync();
                try
                {
                    if (!queued)
                    {
                        Interlocked.Add(ref _queueCount, count);
                        queued = true;
                    }

                    if (CanAcquired())
                    {
                        acquired = true;
                        _acquired += count;
                    }
                    else _availableEvent.Reset();
                }
                finally { _lock.Release(); }

                if (!acquired)
                {
                    if (wait) _availableEvent.Wait();
                    else return null;
                }
            }
            return new SimpleAsyncScope(() => Release(count));
        }
        finally { if (queued) Interlocked.Add(ref _queueCount, -count); }
    }

    protected virtual int GetAcceptedLimit(int limit) => limit;

    protected virtual bool CanAcquired() => _acquired < _limit;

    public void GetRateStatistics(out int availableCountAvg, out int queueCountAvg)
    {
        availableCountAvg = _availableCounts.Count > 0 ? (int)_availableCounts.Average() : 0;
        queueCountAvg = _queueCounts.Count > 0 ? (int)_queueCounts.Average() : 0;
    }

    public void CollectRate(int movingAverageRange)
    {
        if (_queueCounts.Count == movingAverageRange) _queueCounts.TryDequeue(out var _);
        if (_availableCounts.Count == movingAverageRange) _availableCounts.TryDequeue(out var _);
        var (_, _, available, queueCount) = State;
        _queueCounts.Enqueue(queueCount);
        _availableCounts.Enqueue(available);
    }

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        _availableEvent.Dispose();
        _lock.Dispose();
    }
}