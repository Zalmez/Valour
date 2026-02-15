namespace Valour.Shared.Models;

public enum VoiceModerationActionType
{
    Mute = 0,
    Kick = 1
}

public class VoiceModerationEvent
{
    public long ChannelId { get; set; }

    public long ModeratorUserId { get; set; }

    public long TargetUserId { get; set; }

    public VoiceModerationActionType Action { get; set; }
}
