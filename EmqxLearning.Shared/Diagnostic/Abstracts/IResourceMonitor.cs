namespace EmqxLearning.Shared.Diagnostic.Abstracts;

public interface IResourceMonitor
{
    double TotalCores { get; }

    void Start();
    void Stop();
    double GetCpuUsage();
    double GetMemoryUsage();
    void SetMonitor(Func<double, double, Task> monitorCallback, double interval = 10000);
}