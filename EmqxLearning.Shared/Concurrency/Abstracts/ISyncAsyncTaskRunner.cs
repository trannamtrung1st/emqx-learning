namespace EmqxLearning.Shared.Concurrency.Abstracts;

public interface ISyncAsyncTaskRunner
{
    Task RunSyncAsync(IDisposable asyncScope, Func<IDisposable, Task> task, bool longRunning = true);
}