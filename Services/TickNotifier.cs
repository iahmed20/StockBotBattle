// Services/TickNotifier.cs
// Lets running strategies wait for the next batch of prices from PriceTickerService
public class TickNotifier
{
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitForTickAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref _next).Task.WaitAsync(cancellationToken);

    public void Publish()
    {
        var current = Interlocked.Exchange(ref _next, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        current.TrySetResult();
    }
}
