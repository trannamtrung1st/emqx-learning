using System.Collections.Concurrent;
using EmqxLearning.Shared.Concurrency.Abstracts;

namespace EmqxLearning.Shared.Concurrency;

public class InMemoryLockManager : IInMemoryLockManager, IDistributedLockManager, IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, LockObject> _lockMap;

    public InMemoryLockManager()
    {
        _lockMap = new();
    }

    public ILock Acquire(string key, TimeSpan? expiry = null, TimeSpan? timeout = null, int retries = 3)
    {
        using var timeoutCts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        LockObject lockObj;
        lock (_lockMap)
        {
            lockObj = _lockMap.GetOrAdd(key, (key) => new LockObject(key, Release, expiry));
            lockObj.ActiveCount++;
        }

        while (true)
        {
            lock (lockObj)
            {
                if (lockObj.ReadyEvent.IsSet)
                {
                    lockObj.SetAcquired();
                    lockObj.ReadyEvent.Reset();
                    return lockObj;
                }
            }
            lockObj.ReadyEvent.Wait(cancellationToken: timeoutCts.Token);
        }
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var @lock in _lockMap.Values)
            @lock.Release();
    }

    public void MutexAccess(string key, Action action)
    {
        using var mutex = Acquire(key);
        action();
    }

    public async Task MutexAccess(string key, Func<Task> action)
    {
        using var mutex = Acquire(key);
        await action();
    }

    public T MutexAccess<T>(string key, Func<T> task)
    {
        using var mutex = Acquire(key);
        return task();
    }

    public async Task<T> MutexAccess<T>(string key, Func<Task<T>> task)
    {
        using var mutex = Acquire(key);
        return await task();
    }

    private void Release(LockObject lockObj)
    {
        lock (_lockMap)
        {
            lockObj.ActiveCount--;
            if (lockObj.ActiveCount <= 0)
            {
                _lockMap.Remove(lockObj.Key, out _);
                lockObj.HandleLockRemoved();
            }
            else lockObj.SetReady();
        }
    }

    class LockObject : ILock
    {
        private static readonly TimeSpan DefaultExpiry = TimeSpan.FromSeconds(30);
        private readonly Action<LockObject> _onRelease;
        private readonly TimeSpan _expiry;
        private CancellationTokenSource _currentExpiryCts;
        private CancellationTokenRegistration _currentReg;

        public LockObject(string key, Action<LockObject> onRelease, TimeSpan? expiry = null)
        {
            _expiry = expiry ?? DefaultExpiry;
            _onRelease = onRelease;
            Key = key;
            ActiveCount = 0;
            ReadyEvent = new(initialState: true);
        }

        public string Key { get; }
        public int ActiveCount { get; set; }
        public ManualResetEventSlim ReadyEvent { get; }

        public void Dispose() => Release();

        public void Release() => _onRelease(this);

        public void SetAcquired()
        {
            SetExpiryInterval();
            ReadyEvent.Reset();
        }

        public void SetReady()
        {
            _currentExpiryCts?.Dispose();
            _currentExpiryCts = null;
            _currentReg.Dispose();
            _currentReg = default;
            ReadyEvent.Set();
        }

        public void HandleLockRemoved()
        {
            _currentExpiryCts?.Dispose();
            _currentExpiryCts = null;
            _currentReg.Dispose();
            _currentReg = default;
            ReadyEvent.Dispose();
        }

        private void SetExpiryInterval()
        {
            _currentExpiryCts?.Dispose();
            _currentExpiryCts = new CancellationTokenSource(_expiry);
            _currentReg = _currentExpiryCts.Token.Register(() =>
            {
                using (_currentExpiryCts) using (_currentReg) { Release(); }
            });
        }
    }
}