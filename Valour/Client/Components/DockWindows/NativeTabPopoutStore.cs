using System.Collections.Concurrent;

namespace Valour.Client.Components.DockWindows;

public static class NativeTabPopoutStore
{
    private static readonly ConcurrentDictionary<string, WindowTabState> PendingTabs = new();

    public static string Add(WindowTabState tabState)
    {
        var key = Guid.NewGuid().ToString("N");
        PendingTabs[key] = tabState;
        return key;
    }

    public static bool TryTake(string key, out WindowTabState? tabState)
    {
        return PendingTabs.TryRemove(key, out tabState);
    }
}
