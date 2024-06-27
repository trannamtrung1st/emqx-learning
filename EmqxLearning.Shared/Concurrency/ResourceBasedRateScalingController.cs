using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using EmqxLearning.Shared.Concurrency.Abstracts;
using EmqxLearning.Shared.Concurrency.Configurations;
using EmqxLearning.Shared.Diagnostic.Abstracts;

namespace EmqxLearning.Shared.Concurrency;

public class ResourceBasedRateScalingController : IRateScalingController, IDisposable
{
    private readonly IResourceMonitor _resourceMonitor;
    private readonly IResourceBasedFuzzyRateScaler _fuzzyRateScaler;
    private readonly ILogger<ResourceBasedRateScalingController> _logger;
    private readonly IOptions<ResourceBasedRateScalingOptions> _options;
    private System.Timers.Timer _rateCollector;
    private bool _resourceMonitorSet = false;
    private double _lastCpu;
    private double _lastMem;
    private bool _enabled;

    public ResourceBasedRateScalingController(
        IResourceBasedFuzzyRateScaler fuzzyRateScaler,
        IResourceMonitor resourceMonitor,
        ILogger<ResourceBasedRateScalingController> logger,
        IOptions<ResourceBasedRateScalingOptions> options)
    {
        _fuzzyRateScaler = fuzzyRateScaler;
        _resourceMonitor = resourceMonitor;
        _logger = logger;
        _options = options;
    }

    public void Start(IEnumerable<IDynamicRateLimiter> rateLimiters)
    {
        if (!_resourceMonitorSet)
        {
            var scalingOptions = _options.Value;
            _resourceMonitorSet = true;
            _resourceMonitor.Collected += (o, e) =>
            {
                if (!_enabled) return;
                try
                {
                    var (cpu, mem) = e;
                    foreach (var rateLimiter in rateLimiters)
                    {
                        var parameters = scalingOptions.Parameters[rateLimiter.Name];
                        ScaleRate(rateLimiter, cpu, mem, parameters);
                    }
                }
                catch (Exception ex) { _logger.LogError(ex, ex.Message); }
            };
        }

        _enabled = true;
    }

    public void Stop() => _enabled = false;

    private void ScaleRate(IDynamicRateLimiter rateLimiter, double cpu, double mem, ScalingParameters parameters)
    {
        int scaleFactor = parameters.ScaleFactor;
        if (_lastCpu > 0 && _lastMem > 0)
        {
            var cpuDiff = Math.Abs(cpu - _lastCpu);
            var memDiff = Math.Abs(mem - _lastMem);
            var biggerDiff = Math.Max(cpuDiff, memDiff);
            scaleFactor -= (int)(scaleFactor * biggerDiff);
        }
        _lastCpu = cpu; _lastMem = mem;
        var rateScale = _fuzzyRateScaler.GetRateScale(cpu, mem, ideal: parameters.IdealUsage, factor: scaleFactor);
        var (rateLimit, acquired, currentAvailable, _) = rateLimiter.State;
        rateLimiter.GetRateStatistics(out var availableCountAvg, out var queueCountAvg);
        int newLimit;
        if (rateScale < 0)
            newLimit = rateLimit + rateScale;
        else if (Math.Min(currentAvailable, availableCountAvg) > parameters.AcceptedAvailablePercentage * rateLimit && queueCountAvg <= parameters.AcceptedQueueCount)
            newLimit = rateLimit - (int)(availableCountAvg * (1 - parameters.AcceptedAvailablePercentage));
        else
            newLimit = rateLimit + rateScale;
        if (newLimit < rateLimiter.InitialLimit) newLimit = rateLimiter.InitialLimit;
        rateLimiter.SetLimit(newLimit);
        _logger.LogInformation(
            "Limiter: {Limiter}\n" +
            "Scale: {Scale} - Acquired: {Acquired} - Available: {Available} - Queue: {QueueCount}\n" +
            "New rate limit: {Limit}",
            rateLimiter.Name, rateScale, acquired, availableCountAvg, queueCountAvg, newLimit);
    }

    public void StartRateCollector(IEnumerable<IDynamicRateLimiter> rateLimiters)
    {
        if (_rateCollector == null)
        {
            var collectorOptions = _options.Value.RateCollectorOptions;
            _rateCollector = new(interval: collectorOptions.Interval);
            _rateCollector.AutoReset = true;
            _rateCollector.Elapsed += (s, e) =>
            {
                foreach (var limiter in rateLimiters)
                    limiter.CollectRate(collectorOptions.MovingAverageRange);
            };
        }
        _rateCollector.Start();
    }

    public void StopRateCollector() => _rateCollector?.Stop();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _resourceMonitor?.Stop();
        _rateCollector?.Dispose();
    }
}