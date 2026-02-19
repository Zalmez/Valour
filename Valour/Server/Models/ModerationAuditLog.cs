using Valour.Shared.Models;
using Valour.Shared.Models.Staff;

namespace Valour.Server.Models;

public class ModerationAuditLog : ServerModel<long>, ISharedModerationAuditLog, ISharedPlanetModel
{
    public long PlanetId { get; set; }
    public long? ActorUserId { get; set; }
    public long? TargetUserId { get; set; }
    public long? TargetMemberId { get; set; }
    public long? MessageId { get; set; }
    public Guid? TriggerId { get; set; }
    public ModerationActionSource Source { get; set; }
    public ModerationActionType ActionType { get; set; }
    public string? Details { get; set; }
    public DateTime TimeCreated { get; set; }
}
