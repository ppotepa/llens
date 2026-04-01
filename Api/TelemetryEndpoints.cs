using Llens.Observability;

namespace Llens.Api;

public static class TelemetryEndpoints
{
    public static void MapTelemetryRoutes(this WebApplication app)
    {
        app.MapGet("/api/telemetry", (QueryTelemetry telemetry) =>
            Results.Ok(telemetry.Snapshot()));
    }
}
