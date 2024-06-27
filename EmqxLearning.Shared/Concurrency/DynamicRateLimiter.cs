using EmqxLearning.Shared.Concurrency.Abstracts;
using EmqxLearning.Shared.Concurrency.Configurations;

namespace EmqxLearning.Shared.Concurrency;

public class DynamicRateLimiter : IDynamicRateLimiter, IDisposable
{
    private readonly ManualResetEventSlim _availableEvent;
    private readonly object _lock = new();
    private readonly RateLimiterOptions _limiterOptions;
    private readonly Queue<long> _queueCounts = new();
    private readonly Queue<long> _availableCounts = new();
    private long _limit = 0;
    private long _queueCount = 0;
    private long _acquired = 0;

    public DynamicRateLimiter(RateLimiterOptions limiterOptions)
    {
        _availableEvent = new ManualResetEventSlim();
        _limiterOptions = limiterOptions;
        SetLimit(limit: _limiterOptions.InitialLimit);
    }

    public string Name => _limiterOptions.Name;
    public long InitialLimit => _limiterOptions.InitialLimit;
    public (long Limit, long Acquired, long Available, long QueueCount) State
    {
        get
        {
            lock (_lock)
            {
                return (_limit, _acquired, _limit - _acquired, _queueCount);
            }
        }
    }

    public IDisposable Acquire(long count)
        => AcquireCore(count, wait: true);

    protected virtual void Release(long count)
    {
        lock (_lock)
        {
            if (_acquired > 0)
            {
                _acquired -= count;
                _availableEvent.Set();
            }
        }
    }

    public long SetLimit(long limit)
    {
        var acceptedLimit = GetAcceptedLimit(limit);
        lock (_lock)
        {
            var prevLimit = _limit;
            _limit = acceptedLimit;
            if (_limit > prevLimit) _availableEvent.Set();
        }
        return acceptedLimit;
    }

    public long ResetLimit() => SetLimit(limit: _limiterOptions.InitialLimit);

    public bool TryAcquire(long count, out IDisposable scope)
    {
        scope = AcquireCore(count, wait: false);
        return scope != null;
    }

    protected virtual IDisposable AcquireCore(long count, bool wait)
    {
        if (_limit == 0)
            return null;
        bool queued = false;
        bool acquired = false;
        try
        {
            while (!acquired)
            {
                lock (_lock)
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

                if (!acquired)
                {
                    if (wait) _availableEvent.Wait();
                    else return null;
                }
            }
            return new SimpleScope(() => Release(count));
        }
        finally { if (queued) Interlocked.Add(ref _queueCount, -count); }
    }

    protected virtual long GetAcceptedLimit(long limit) => limit;

    protected virtual bool CanAcquired() => _acquired < _limit;

    public void GetRateStatistics(out long availableCountAvg, out long queueCountAvg)
    {
        availableCountAvg = _availableCounts.Count > 0 ? (long)_availableCounts.Average() : 0;
        queueCountAvg = _queueCounts.Count > 0 ? (long)_queueCounts.Average() : 0;
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
    }
}