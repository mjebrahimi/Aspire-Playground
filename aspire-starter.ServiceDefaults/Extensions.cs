using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Exceptions;
using Serilog.Exceptions.Core;
using Serilog.Exceptions.EntityFrameworkCore.Destructurers;
using Serilog.Exceptions.MsSqlServer.Destructurers;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    public static IHostApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder)
    {
        builder.AddSerilog();

        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static WebApplicationBuilder AddSerilog(this WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();

        //For very high-throughput scenarios, it's better to use ZLogger nuget package (a Zero Allocation and High Performance logger)
        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)

                //Enrich logs with useful meta data
                .Enrich.FromLogContext()
                .Enrich.WithSpan()
                .Enrich.WithClientIp()
                .Enrich.WithProcessName()
                .Enrich.WithEnvironmentName()
                .Enrich.WithDemystifiedStackTraces()
                .Enrich.WithRequestHeader("User-Agent", "UserAgent") // Enrich logs with request headers like api-key, auth token, etc.
                .Enrich.WithExceptionDetails(new DestructuringOptionsBuilder()
                       .WithDefaultDestructurers()
                       .WithDestructurers([new SqlExceptionDestructurer(), new DbUpdateExceptionDestructurer()]))

                //Console logging is synchronous and this can cause bottlenecks in some deployment scenarios.
                //For high-volume console logging, consider using Serilog.Sinks.Async to move console writes to a background thread
                .WriteTo.Async(sink => sink.Console())

                //You can also write logs to File, ElasticSearch, Seq, Application Insights, AWS Cloud Watch, etc.
                .WriteTo.Seq(context.Configuration.GetConnectionString("seq")!)

                //Send logs to Aspire Dashboard using OpenTelemetry
                .WriteTo.OpenTelemetry(options =>
                {
                    options.Endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]!;
                    options.ResourceAttributes["service.name"] = builder.Configuration["OTEL_SERVICE_NAME"] ?? "Unknown";

                    var headers = builder.Configuration["OTEL_EXPORTER_OTLP_HEADERS"]?.Split(',') ?? [];
                    foreach (var header in headers)
                    {
                        var (key, value) = header.Split('=') switch
                        {
                        [string k, string v] => (k, v),
                            var v => throw new Exception($"Invalid header format {v}")
                        };
                        options.Headers[key] = value;
                    }

                    var attributes = builder.Configuration["OTEL_RESOURCE_ATTRIBUTES"]?.Split(',') ?? [];
                    foreach (var attribute in attributes)
                    {
                        var (key, value) = attribute.Split('=') switch
                        {
                        [string k, string v] => (k, v),
                            var v => throw new Exception($"Invalid header format {v}")
                        };
                        options.ResourceAttributes[key] = value;
                    }
                });
        });

        return builder;
    }

    public static IHostApplicationBuilder ConfigureOpenTelemetry(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddBuiltInMeters();
            })
            .WithTracing(tracing =>
            {
                if (builder.Environment.IsDevelopment())
                {
                    // We want to view all traces in development
                    tracing.SetSampler(new AlwaysOnSampler());
                }
                else
                {
                    // How much percentage of traces to sample on the production
                    tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.5)));
                }

                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation(options => options.SetDbStatementForText = true)
                    .AddSqlClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                        options.SetDbStatementForText = true;
                    });
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static IHostApplicationBuilder AddOpenTelemetryExporters(this IHostApplicationBuilder builder)
    {
        var useOtlpExporter = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            // We use Serilog instead of the default OpenTelemetry logger
            //builder.Services.Configure<OpenTelemetryLoggerOptions>(logging => logging.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddOtlpExporter());
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing =>
            {
                //Export traces to Aspire Dashboard
                tracing.AddOtlpExporter();

                var seq = builder.Configuration.GetConnectionString("seq");
                if (string.IsNullOrWhiteSpace(seq) is false)
                {
                    //Export traces to Seq
                    tracing.AddOtlpExporter(options => options.Endpoint = new(seq));
                }
            });
        }

        // Uncomment the following lines to enable the Prometheus exporter (requires the OpenTelemetry.Exporter.Prometheus.AspNetCore package)
        // builder.Services.AddOpenTelemetry()
        //    .WithMetrics(metrics => metrics.AddPrometheusExporter());

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.Exporter package)
        // builder.Services.AddOpenTelemetry()
        //    .UseAzureMonitor();

        return builder;
    }

    public static IHostApplicationBuilder AddDefaultHealthChecks(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Uncomment the following line to enable the Prometheus endpoint (requires the OpenTelemetry.Exporter.Prometheus.AspNetCore package)
        // app.MapPrometheusScrapingEndpoint();

        // All health checks must pass for app to be considered ready to accept traffic after starting
        app.MapHealthChecks("/health");

        // Only health checks tagged with the "live" tag must pass for app to be considered alive
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains("live")
        });

        return app;
    }

    private static MeterProviderBuilder AddBuiltInMeters(this MeterProviderBuilder meterProviderBuilder)
    {
        return meterProviderBuilder.AddMeter(
            "Microsoft.AspNetCore.Hosting",
            "Microsoft.AspNetCore.Server.Kestrel",
            "System.Net.Http",
            //The following metrics don't work! //https://github.com/dotnet/SqlClient/issues/2211
            "Microsoft.EntityFrameworkCore",
            "Microsoft.Data.SqlClient.EventSource");

        // Other built-in meters that can be added
        // or using metrics.AddAspNetCoreInstrumentation(), metrics.AddHttpClientInstrumentation methods
        //    .AddMeter("Microsoft.AspNetCore.Http.Connections")
        //    .AddMeter("Microsoft.AspNetCore.Routing")
        //    .AddMeter("Microsoft.AspNetCore.Diagnostics")
        //    .AddMeter("Microsoft.AspNetCore.RateLimiting");
        //    .AddMeter("System.Net.NameResolution");
    }

}
