using Valour.Server.Database;
using Valour.Server.Models;
using Valour.Shared.Models;
using Valour.Shared.Models.Staff;
using Valour.Shared.Queries;

namespace Valour.Server.Services;

public class ModerationAuditService
{
    private readonly ValourDb _db;
    private readonly ILogger<ModerationAuditService> _logger;

    public ModerationAuditService(ValourDb db, ILogger<ModerationAuditService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task LogAsync(
        long planetId,
        ModerationActionSource source,
        ModerationActionType actionType,
        long? actorUserId = null,
        long? targetUserId = null,
        long? targetMemberId = null,
        long? messageId = null,
        Guid? triggerId = null,
        string? details = null,
        DateTime? timeCreated = null)
    {
        try
        {
            var log = new Valour.Database.ModerationAuditLog
            {
                Id = IdManager.Generate(),
                PlanetId = planetId,
                Source = source,
                ActionType = actionType,
                ActorUserId = actorUserId,
                TargetUserId = targetUserId,
                TargetMemberId = targetMemberId,
                MessageId = messageId,
                TriggerId = triggerId,
                Details = details,
                TimeCreated = timeCreated ?? DateTime.UtcNow
            };

            await _db.ModerationAuditLogs.AddAsync(log);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to write moderation audit log. planetId={PlanetId}, source={Source}, actionType={ActionType}",
                planetId,
                source,
                actionType);
        }
    }

    public async Task<QueryResponse<ModerationAuditLog>> QueryPlanetLogsAsync(long planetId, QueryRequest request)
    {
        var take = Math.Min(100, request.Take);
        var skip = request.Skip;

        var logs = _db.ModerationAuditLogs
            .AsNoTracking()
            .Where(x => x.PlanetId == planetId);

        var query =
            from log in logs
            join actorUser in _db.Users.AsNoTracking() on log.ActorUserId equals actorUser.Id into actorJoin
            from actor in actorJoin.DefaultIfEmpty()
            join targetUser in _db.Users.AsNoTracking() on log.TargetUserId equals targetUser.Id into targetJoin
            from target in targetJoin.DefaultIfEmpty()
            select new
            {
                Log = log,
                Actor = actor,
                Target = target
            };

        var search = request.Options?.Filters?.GetValueOrDefault("search");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var lowered = search.ToLowerInvariant();
            query = query.Where(x =>
                (x.Log.Details != null && EF.Functions.ILike(x.Log.Details.ToLower(), $"%{lowered}%")) ||
                (x.Actor != null && EF.Functions.ILike(x.Actor.Name.ToLower(), $"%{lowered}%")) ||
                (x.Target != null && EF.Functions.ILike(x.Target.Name.ToLower(), $"%{lowered}%")));
        }

        var sortField = request.Options?.Sort?.Field;
        var sortDesc = request.Options?.Sort?.Descending ?? true;
        if (string.IsNullOrWhiteSpace(sortField))
            sortDesc = true;

        query = sortField switch
        {
            "action" => sortDesc
                ? query.OrderByDescending(x => x.Log.ActionType)
                : query.OrderBy(x => x.Log.ActionType),
            "source" => sortDesc
                ? query.OrderByDescending(x => x.Log.Source)
                : query.OrderBy(x => x.Log.Source),
            "actor" => sortDesc
                ? query.OrderByDescending(x => x.Actor!.Name)
                : query.OrderBy(x => x.Actor!.Name),
            "target" => sortDesc
                ? query.OrderByDescending(x => x.Target!.Name)
                : query.OrderBy(x => x.Target!.Name),
            _ => sortDesc
                ? query.OrderByDescending(x => x.Log.TimeCreated)
                : query.OrderBy(x => x.Log.TimeCreated)
        };

        var total = await query.CountAsync();
        var items = await query
            .Skip(skip)
            .Take(take)
            .Select(x => x.Log.ToModel())
            .ToListAsync();

        return new QueryResponse<ModerationAuditLog>
        {
            Items = items,
            TotalCount = total
        };
    }
}
