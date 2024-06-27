using EmqxLearning.Shared.Concurrency.Abstracts;

namespace EmqxLearning.Shared.Concurrency;

public class SyncAsyncTaskRunner : ISyncAsyncTaskRunner
{
    public async Task RunSyncAsync(IDisposable asyncScope, Func<IDisposable, Task> task, bool longRunning = true)
    {
        if (asyncScope != null)
            await RunAsync(asyncScope, task, longRunning);
        else
            await task(new SimpleScope());
    }

    protected virtual Task RunAsync(IDisposable asyncScope, Func<IDisposable, Task> task, bool longRunning)
    {
        Task asyncTask = null;
        Task MainTask() => task(new SimpleScope(asyncScope, asyncTask));

        if (longRunning)
        {
            asyncTask = Task.Factory.StartNew(
                function: MainTask,
                creationOptions: TaskCreationOptions.LongRunning);
        }
        else
        {
            _ = Task.Run(MainTask);
        }

        return Task.CompletedTask;
    }
}