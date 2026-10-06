using Game.Api.Health;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Game.Api.Observability;

public static class ObservabilityExtensions
{
    /// <summary>
    /// Traces, metrics and logs through OpenTelemetry. They're exported over OTLP only when
    /// OTEL_EXPORTER_OTLP_ENDPOINT is set (for example to the free Aspire dashboard in docker
    /// compose), so nothing changes when no collector is running.
    /// </summary>
    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder)
    {
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: "card-arena-api",
                serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(tracing => tracing
                .AddSource(GameTelemetry.Name)
                .AddAspNetCoreInstrumentation(options =>
                    // Probes hit these every few seconds; tracing them would bury real requests.
                    options.Filter = context => !context.Request.Path.StartsWithSegments(HealthEndpoints.BasePath))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddMeter(GameTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            otel.UseOtlpExporter();
        }

        return builder;
    }
}
