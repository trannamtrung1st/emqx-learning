using System.Diagnostics;
using System.Runtime.InteropServices;
using EmqxLearning.Shared.Services.Abstracts;

namespace EmqxLearning.Shared.Services;

public class ResourceMonitor : IResourceMonitor
{
    private System.Timers.Timer _currentTimer;
    private DateTime _lastCpuTime;
    private long _lastCpuUsage;

    public ResourceMonitor()
    {
        _lastCpuTime = DateTime.UtcNow;
        _lastCpuUsage = GetCpuUsageMs();
    }

    public double GetCpuUsage()
    {
        var currentTime = DateTime.UtcNow;
        var currentUsageMs = GetCpuUsageMs();
        var totalTimeMs = (currentTime - _lastCpuTime).TotalMilliseconds;
        var cpuUtil = (currentUsageMs - _lastCpuUsage) / totalTimeMs;
        _lastCpuTime = currentTime;
        _lastCpuUsage = currentUsageMs;
        return cpuUtil;
    }

    private long GetCpuUsageMs()
    {
        var cpuStat = ExecuteCommand("cat /sys/fs/cgroup/cpu.stat");
        var usage = cpuStat.Split(Environment.NewLine)[0].Split(" ")[1];
        return long.Parse(usage) / 1000;
    }

    public double GetMemoryUsage()
    {
        var maxMem = ExecuteCommand("cat /sys/fs/cgroup/memory.max");
        var currentMem = ExecuteCommand("cat /sys/fs/cgroup/memory.current");
        var total = double.Parse(maxMem);
        var used = double.Parse(currentMem);
        return used / total;
    }

    public void SetMonitor(Func<double, double, Task> monitorCallback, double interval = 10000)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return;
        _currentTimer?.Stop();
        _currentTimer = new System.Timers.Timer(interval);
        _currentTimer.Elapsed += async (s, e) =>
        {
            var cpuUsage = GetCpuUsage();
            var memUsage = GetMemoryUsage();
            await monitorCallback(cpuUsage, memUsage);
        };
        _currentTimer.AutoReset = true;
    }

    public void Start() => _currentTimer?.Start();

    public void Stop() => _currentTimer?.Stop();

    private string ExecuteCommand(string command)
    {
        string output = null;
        var info = new ProcessStartInfo();
        info.FileName = "/bin/sh";
        info.Arguments = $"-c \"{command}\"";
        info.RedirectStandardOutput = true;

        using (var process = Process.Start(info))
        {
            output = process.StandardOutput.ReadToEnd();
            return output;
        }
    }
}