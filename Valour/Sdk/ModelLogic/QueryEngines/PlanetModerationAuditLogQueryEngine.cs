namespace Valour.Sdk.ModelLogic.QueryEngines;

public class PlanetModerationAuditLogQueryEngine : ModelQueryEngine<ModerationAuditLog>
{
    public PlanetModerationAuditLogQueryEngine(Planet planet, int cacheSize = 100) :
        base(planet.Node, $"api/planets/{planet.Id}/moderation/audit/query", cacheSize)
    {
    }
}
