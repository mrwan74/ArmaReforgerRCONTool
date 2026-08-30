using Avalonia;
using Avalonia.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aptabase.Avalonia;

public static class AptabaseExtensions
{
    private static IAptabaseClient? _instance;

    public static IAptabaseClient Instance =>
        _instance ?? throw new AptabaseConfigurationException("Aptabase is not initialized. Call UseAptabase or AddAptabase first.");

    public static bool IsInitialized => _instance != null;

    public static IServiceCollection AddAptabase(
        this IServiceCollection services,
        string appKey,
        AptabaseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        services.AddSingleton<IAptabaseClient>(sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            var client = CreateClient(appKey, options, loggerFactory);
            _instance = client;

            if (options?.EnableCrashReporting ?? true)
            {
                _ = new AptabaseCrashReporter(client, options, loggerFactory.CreateLogger<AptabaseCrashReporter>());
            }

            return client;
        });

        return services;
    }

    public static AppBuilder UseAptabase(
        this AppBuilder builder,
        string appKey,
        AptabaseOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        loggerFactory ??= NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger("Aptabase.Avalonia");

        if (options?.CaptureAvaloniaFrameworkLogs ?? true)
        {
            var minimumLevel = options?.AvaloniaLogEventLevel ?? LogEventLevel.Warning;
            Logger.Sink = new AptabaseLogSink(minimumLevel, logger);
        }

        _instance = CreateClient(appKey, options, loggerFactory);

        if (options?.EnableCrashReporting ?? true)
        {
            _ = new AptabaseCrashReporter(_instance, options, loggerFactory.CreateLogger<AptabaseCrashReporter>());
        }

        return builder;
    }

    private static IAptabaseClient CreateClient(string appKey, AptabaseOptions? options, ILoggerFactory loggerFactory)
    {
        IAptabaseClient client;

        if (options?.EnablePersistence is not true)
        {
            client = new AptabaseClient(appKey, options, loggerFactory.CreateLogger<AptabaseClient>());
        }
        else
        {
            client = new AptabasePersistentClient(appKey, options, loggerFactory.CreateLogger<AptabasePersistentClient>());
        }

        return client;
    }
}