using Azure.Monitor.OpenTelemetry.Exporter;
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
    /// compose), and to Application Insights only when APPLICATIONINSIGHTS_CONNECTION_STRING is set,
    /// so nothing changes when no collector is running.
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

        // In Azure the same telemetry goes to Application Insights instead.
        var appInsights = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(appInsights))
        {
            otel.WithTracing(tracing => tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsights))
                .WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsights));
            builder.Logging.AddOpenTelemetry(logging =>
                logging.AddAzureMonitorLogExporter(o => o.ConnectionString = appInsights));
        }

        return builder;
    }
}
