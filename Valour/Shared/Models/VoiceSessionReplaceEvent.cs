namespace Valour.Shared.Models;

public class VoiceSessionReplaceEvent
{
    public long ChannelId { get; set; }

    public string SessionId { get; set; } = string.Empty;
}
