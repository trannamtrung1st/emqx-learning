using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;
using EmqxLearning.Shared.Diagnostic.Abstracts;

namespace EmqxLearning.Shared.Diagnostic;

public class RateMonitor : IRateMonitor, IDisposable
{
    private readonly ConcurrentDictionary<string, CategoryCount> _categoryMap;
    private readonly ILogger<RateMonitor> _logger;
    private readonly System.Timers.Timer _reportTimer;

    public RateMonitor(ILogger<RateMonitor> logger)
    {
        _categoryMap = new();
        _logger = logger;
        _reportTimer = new(interval: 5000);
        _reportTimer.AutoReset = true;
        _reportTimer.Elapsed += HandleReport;
    }

    public void Capture(string category, int count)
    {
        var categoryCount = _categoryMap.GetOrAdd(category, (key) => new CategoryCount(key));
        categoryCount.Increase(count);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _reportTimer.Elapsed -= HandleReport;
        _reportTimer.Dispose();
    }

    public int GetRate(string category)
    {
        var categoryCount = _categoryMap.GetOrAdd(category, (key) => new CategoryCount(key));
        return categoryCount.GetRate();
    }

    private void HandleReport(object e, EventArgs args) => PrintAll();

    public void PrintAll()
    {
        if (_categoryMap.Values.Count == 0)
            return;

        var sb = new StringBuilder();
        sb.AppendLine("=== Rates report ===");

        foreach (var category in _categoryMap.Values)
        {
            var rate = category.GetRate();
            sb.AppendLine($"{category.Category}: {rate}/s");
        }

        _logger.LogDebug(sb.ToString());
    }

    public void StartReport() => _reportTimer.Start();

    class CategoryCount
    {
        const int DefaultRetentionSeconds = 60;

        public CategoryCount(string category)
        {
            Category = category;
            CountPerSecond = new();
        }

        public string Category { get; }
        public ConcurrentDictionary<long, CountRef> CountPerSecond { get; }

        public void Increase(int count)
        {
            long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var countRef = CountPerSecond.GetOrAdd(second, (_) => new CountRef());
            countRef.Increase(count);

            long retentionFrom = second - DefaultRetentionSeconds;
            var removed = CountPerSecond.Keys.Where(s => s < retentionFrom);
            foreach (var recordKey in removed)
                CountPerSecond.TryRemove(recordKey, out _);
        }

        public int GetRate()
        {
            if (CountPerSecond.IsEmpty)
                return 0;

            long current = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long past = current - 5;
            var lastCounts = CountPerSecond.Where(kvp => kvp.Key > past && kvp.Key < current).ToArray();
            if (lastCounts.Length == 0)
                return 0;

            var avgRate = lastCounts.Average(kvp => kvp.Value.Count);
            return (int)avgRate;
        }
    }

    class CountRef
    {
        private int _count;
        public int Count => _count;

        public void Increase(int count) => Interlocked.Add(ref _count, count);
    }
}