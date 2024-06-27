namespace EmqxLearning.Shared.Diagnostic.Abstracts;

public interface IRateMonitor
{
    void Capture(string category, int count);
    int GetRate(string category);
    void PrintAll();
    void StartReport();
}
