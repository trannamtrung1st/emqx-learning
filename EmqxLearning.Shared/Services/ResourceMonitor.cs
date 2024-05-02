using System.Diagnostics;
using System.Runtime.InteropServices;
using EmqxLearning.Shared.Services.Abstracts;

namespace EmqxLearning.Shared.Services;

public class ResourceMonitor : IResourceMonitor
{
    private System.Timers.Timer _currentTimer;

    public double GetCpuUsage()
    {
        var output = ExecuteCommand("mpstat");
        var outputParts = output.Split(Environment.NewLine);
        var usageInfo = outputParts[outputParts.Length - 2];
        var usageParts = usageInfo.Split(" ", StringSplitOptions.RemoveEmptyEntries);
        var idleCpu = usageParts[usageParts.Length - 1];
        var cpuUsage = 1 - (double.Parse(idleCpu) / 100);
        return cpuUsage;
    }

    public double GetMemoryUsage()
    {
        var output = ExecuteCommand("free -m");
        var lines = output.Split(Environment.NewLine);
        var memory = lines[1].Split(" ", StringSplitOptions.RemoveEmptyEntries);
        var total = double.Parse(memory[1]);
        var used = double.Parse(memory[2]);
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