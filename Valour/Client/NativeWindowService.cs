namespace Valour.Client;

public interface INativeWindowService
{
    bool SupportsTabPopout { get; }

    Task<bool> TryOpenTabPopoutWindow(string popoutKey, string title);
}

public sealed class NoopNativeWindowService : INativeWindowService
{
    public bool SupportsTabPopout => false;

    public Task<bool> TryOpenTabPopoutWindow(string popoutKey, string title)
    {
        return Task.FromResult(false);
    }
}
