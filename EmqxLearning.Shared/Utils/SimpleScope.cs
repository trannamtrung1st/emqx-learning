
namespace EmqxLearning.Shared.Utils;

public sealed class SimpleScope : IDisposable
{
    private readonly Action _onDispose;

    public SimpleScope()
    {
    }

    public SimpleScope(Action onDispose)
    {
        _onDispose = onDispose;
    }

    public void Dispose() => _onDispose?.Invoke();
}

public sealed class SimpleAsyncScope : IAsyncDisposable
{
    private readonly Func<Task> _onDispose;

    public SimpleAsyncScope()
    {
    }

    public SimpleAsyncScope(Func<Task> onDispose)
    {
        _onDispose = onDispose;
    }

    public async ValueTask DisposeAsync()
    {
        if (_onDispose != null) await _onDispose();
    }
}