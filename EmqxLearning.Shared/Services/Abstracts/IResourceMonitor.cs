namespace EmqxLearning.Shared.Services.Abstracts;

public interface IResourceMonitor
{
    double GetCpuUsage();
    double GetMemoryUsage();
    void Monitor(Func<double, double, Task> monitorCallback, double interval = 10000);
}