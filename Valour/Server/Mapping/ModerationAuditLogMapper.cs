namespace Valour.Server.Mapping;

public static class ModerationAuditLogMapper
{
    public static ModerationAuditLog ToModel(this Valour.Database.ModerationAuditLog log)
    {
        if (log is null)
            return null;

        return new ModerationAuditLog
        {
            Id = log.Id,
            PlanetId = log.PlanetId,
            ActorUserId = log.ActorUserId,
            TargetUserId = log.TargetUserId,
            TargetMemberId = log.TargetMemberId,
            MessageId = log.MessageId,
            TriggerId = log.TriggerId,
            Source = log.Source,
            ActionType = log.ActionType,
            Details = log.Details,
            TimeCreated = log.TimeCreated
        };
    }

    public static Valour.Database.ModerationAuditLog ToDatabase(this ModerationAuditLog log)
    {
        if (log is null)
            return null;

        return new Valour.Database.ModerationAuditLog
        {
            Id = log.Id,
            PlanetId = log.PlanetId,
            ActorUserId = log.ActorUserId,
            TargetUserId = log.TargetUserId,
            TargetMemberId = log.TargetMemberId,
            MessageId = log.MessageId,
            TriggerId = log.TriggerId,
            Source = log.Source,
            ActionType = log.ActionType,
            Details = log.Details,
            TimeCreated = log.TimeCreated
        };
    }
}
