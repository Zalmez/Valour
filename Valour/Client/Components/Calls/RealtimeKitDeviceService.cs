using Microsoft.JSInterop;

namespace Valour.Client.Components.Calls;

public sealed class RealtimeKitDeviceService : IAsyncDisposable
{
    private const string ModulePath = "./_content/Valour.Client/Components/Calls/RealtimeKitComponent.razor.js";

    private readonly IJSRuntime _jsRuntime;
    private Task<IJSObjectReference>? _moduleTask;

    public RealtimeKitDeviceService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    private Task<IJSObjectReference> ImportModuleAsync()
    {
        return _jsRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath).AsTask();
    }

    private async Task<IJSObjectReference> GetModuleAsync()
    {
        if (_moduleTask is null || _moduleTask.IsFaulted || _moduleTask.IsCanceled)
        {
            _moduleTask = ImportModuleAsync();
        }

        try
        {
            return await _moduleTask;
        }
        catch
        {
            // Don't permanently cache failed imports; allow retry when WebView context is ready.
            _moduleTask = null;
            throw;
        }
    }

    public async Task<InputMic[]> GetAudioInputDevicesAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<InputMic[]>("getAudioInputDevices");
    }

    public async Task<InputMic[]> GetVideoInputDevicesAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<InputMic[]>("getVideoInputDevices");
    }

    public async Task<string> GetMicrophonePermissionStateAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string>("getMicrophonePermissionState");
    }

    public async Task<bool> RequestMicrophonePermissionAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("requestMicrophonePermission");
    }

    public async Task<string> GetCameraPermissionStateAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<string>("getCameraPermissionState");
    }

    public async Task<bool> RequestCameraPermissionAsync()
    {
        var module = await GetModuleAsync();
        return await module.InvokeAsync<bool>("requestCameraPermission");
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is null || !_moduleTask.IsCompletedSuccessfully)
            return;

        try
        {
            var module = await _moduleTask;
            await module.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
            // Ignore if runtime already disconnected.
        }
    }
}
