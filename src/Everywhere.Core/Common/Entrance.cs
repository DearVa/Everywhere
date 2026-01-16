#if !DEBUG
using Everywhere.Interop;
#else
#define DISABLE_TELEMETRY
#endif

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Sentry.OpenTelemetry;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using ZLinq;

namespace Everywhere.Common;

public static class Entrance
{
    public static bool SendOnlyNecessaryTelemetry { get; set; }

    public static event EventHandler<UnobservedTaskExceptionEventArgs>? UnobservedTaskExceptionFilter;

#if !DEBUG
    private static Mutex? _appMutex;
#endif

    /// <summary>
    /// Releases the application mutex. Only call this method when the application is exiting.
    /// </summary>
    public static void ReleaseMutex()
    {
#if !DEBUG
        if (_appMutex == null) return;

        _appMutex.ReleaseMutex();
        _appMutex.Dispose();
        _appMutex = null;
#endif
    }

    public static void Initialize()
    {
#if !DISABLE_TELEMETRY
        InitializeTelemetry();
#endif
        InitializeLogger();
        InitializeErrorHandling();
    }

    /// <summary>
    /// Initializes the application mutex to ensure a single instance of the application.
    /// </summary>
    /// <param name="args"></param>
    public static void InitializeApplicationMutex(string[] args)
    {
#if DEBUG
        // axaml designer may launch this code, so we need to set it to null.
        _ = args;
#else
        _appMutex = new Mutex(true, "EverywhereAppMutex", out var createdNew);
        if (createdNew) return;

        if (!args.Contains("--autorun"))
        {
            NativeMessageBox.Show(
                LocaleResolver.Common_Info,
                LocaleResolver.Entrance_EverywhereAlreadyRunning,
                NativeMessageBoxButtons.Ok,
                NativeMessageBoxIcon.Information);
        }

        Environment.Exit(0);
#endif
    }

    private static void InitializeTelemetry()
    {
        var sentry = SentrySdk.Init(o =>
        {
            o.Dsn = "https://25114ca299b74da64aed26ffc2ac072e@o4510145762689024.ingest.us.sentry.io/4510145814069248";
            o.AutoSessionTracking = true;
            o.EnableLogs = true;
#if DEBUG
            o.TracesSampleRate = 1.0;
            o.Environment = "debug";
            o.Debug = true;
#else
            o.TracesSampleRate = 0.2;
            o.Environment = "stable";
#endif
            o.Release = typeof(Entrance).Assembly.GetName().Version?.ToString();

            o.UseOpenTelemetry();
            o.SetBeforeSend(evt =>
                evt.Exception.Segregate()
                    .AsValueEnumerable()
                    .Any(e => e is not OperationCanceledException and not HandledException { IsExpected: true }) ?
                    evt :
                    null);
            o.SetBeforeSendTransaction(transaction => SendOnlyNecessaryTelemetry ? null : transaction);
            o.SetBeforeBreadcrumb(breadcrumb => SendOnlyNecessaryTelemetry ? null : breadcrumb);
        });

        SentrySdk.ConfigureScope(scope =>
        {
            scope.User.Username = null;
            scope.User.IpAddress = null;
            scope.User.Email = null;
        });

        var traceProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource("Everywhere.*")
            .AddSentry()
            .Build();

        AppDomain.CurrentDomain.ProcessExit += delegate
        {
            traceProvider.Dispose();
            sentry.Dispose();
        };
    }

    private static void InitializeLogger()
    {
        var dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Everywhere");

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .Enrich.With<ActivityEnricher>()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                new JsonFormatter(),
                Path.Combine(dataPath, "logs", ".jsonl"),
                rollingInterval: RollingInterval.Day)
#if !DISABLE_TELEMETRY
            .WriteTo.Logger(lc => lc
                .Filter.ByIncludingOnly(logEvent =>
                    logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue) &&
                    sourceContextValue.As<ScalarValue>()?.Value?.ToString()?.StartsWith("Everywhere.") is true)
                .WriteTo.Sentry(LogEventLevel.Error, LogEventLevel.Information))
#endif
            .CreateLogger();
    }

    private static void InitializeErrorHandling()
    {
        AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        {
            Log.Logger.Error(e.ExceptionObject as Exception, "Unhandled Exception");
        };

        TaskScheduler.UnobservedTaskException += static (s, e) =>
        {
            UnobservedTaskExceptionFilter?.Invoke(s, e);
            if (e.Observed) return;

            Log.Logger.Error(e.Exception, "Unobserved Task Exception");
            e.SetObserved();
        };
    }

    private sealed class ActivityEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (Activity.Current is not { } activity) return;

            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    nameof(activity.TraceId),
                    activity.TraceId)
            );
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    nameof(activity.SpanId),
                    activity.SpanId)
            );
            logEvent.AddPropertyIfAbsent(
                propertyFactory.CreateProperty(
                    "ActivityId",
                    activity.Id)
            );
        }
    }
}