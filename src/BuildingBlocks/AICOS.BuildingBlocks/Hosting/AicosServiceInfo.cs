namespace AICOS.BuildingBlocks.Hosting;

/// <summary>Static identity of a microservice.</summary>
/// <param name="Name">Short key used in logs, config and docker (e.g. "order").</param>
/// <param name="DisplayName">Human readable name (e.g. "Order Service").</param>
/// <param name="RoutePrefix">First path segment behind the gateway: /api/v1/{RoutePrefix}/...</param>
/// <param name="Schema">PostgreSQL schema owned by this service.</param>
public sealed record AicosServiceInfo(string Name, string DisplayName, string RoutePrefix, string Schema)
{
    public string ApiBasePath => $"/api/v1/{RoutePrefix}";
}
