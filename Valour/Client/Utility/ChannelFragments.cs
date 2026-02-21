using Microsoft.AspNetCore.Components;
using Valour.Sdk.Models;
using Valour.Shared.Models;

namespace Valour.Client.Utility;

public static class ChannelFragments
{
    private static string GetIcon(ChannelTypeEnum type) => type switch
    {
        ChannelTypeEnum.PlanetChat => "chat-left",
        ChannelTypeEnum.PlanetVoice => "music-note-beamed",
        ChannelTypeEnum.PlanetVideo => "camera-video",
        ChannelTypeEnum.PlanetCategory => "folder",
        _ => "chat-left"
    };

    public static RenderFragment<Channel> ChannelPill => channel => __builder =>
    {
        __builder.OpenElement(0, "span");
        __builder.AddAttribute(1, "class", "channel-pill");
        __builder.OpenElement(2, "i");
        __builder.AddAttribute(3, "class", $"bi bi-{GetIcon(channel.ChannelType)}");
        __builder.CloseElement();
        __builder.AddContent(4, $" {channel.Name}");
        __builder.CloseElement();
    };
}
