namespace EmqxLearning.Shared.Services.Abstracts;

public interface IResourceMonitor
{
    void Start();
    void Stop();
    double GetCpuUsage();
    double GetMemoryUsage();
    void SetMonitor(Func<double, double, Task> monitorCallback, double interval = 10000);
}