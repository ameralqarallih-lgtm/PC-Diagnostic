using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PcDiag.Core.Configuration;
using PcDiag.Storage.Sqlite;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PcDiag.Hosting;

/// <summary>Shared bootstrap for the Agent and the Web host.</summary>
public static class PcDiagHostBuilderExtensions
{
    /// <summary>Size at which a log file rolls over.</summary>
    public const long LogFileSizeLimitBytes = 10 * 1024 * 1024;

    /// <summary>Number of log files kept per application.</summary>
    public const int RetainedLogFiles = 14;

    /// <summary>
    /// Adds the user settings file, validated options, data paths, storage services, database initialization
    /// and Serilog logging.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="applicationName">Short name used for log files, e.g. "agent" or "web".</param>
    public static IHostApplicationBuilder AddPcDiagHost(this IHostApplicationBuilder builder, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        AddUserSettingsFile(builder.Configuration);

        var services = builder.Services;
        services.AddValidatedOptions<StorageOptions, StorageOptionsValidator>(builder.Configuration, StorageOptions.SectionName);
        services.AddValidatedOptions<RecorderOptions, RecorderOptionsValidator>(builder.Configuration, RecorderOptions.SectionName);
        services.AddValidatedOptions<SamplingOptions, SamplingOptionsValidator>(builder.Configuration, SamplingOptions.SectionName);
        services.AddValidatedOptions<EventOptions, EventOptionsValidator>(builder.Configuration, EventOptions.SectionName);
        services.AddOptions<PrivacyOptions>().Bind(builder.Configuration.GetSection(PrivacyOptions.SectionName));
        services.AddValidatedOptions<TriggerOptions, TriggerOptionsValidator>(builder.Configuration, TriggerOptions.SectionName);
        services.AddOptions<EnhancedOptions>().Bind(builder.Configuration.GetSection(EnhancedOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new DataPaths(sp.GetRequiredService<IOptions<StorageOptions>>().Value.DataDirectory));
        services.AddPcDiagStorage();

        // Registered first so databases exist before any other hosted service starts.
        services.Insert(0, ServiceDescriptor.Singleton<IHostedService, DatabaseInitializationService>());

        // The product's own errors and crashes become problem reports in the local outbox (sent only by the Web host, and
        // automatically only with the user's consent).
        services.TryAddSingleton(sp =>
        {
            var capture = new ProblemCapture(sp.GetRequiredService<DataPaths>().ReportsDirectory, applicationName);
            capture.HookCrashes();
            return capture;
        });
        // Each host keeps its own logger (not Serilog's static one): with two hosts in a process (tests, the Agent's
        // launcher), each one's logs and problem reports stay its own.
        services.AddSerilog((sp, logger) => ConfigureLogger(logger, sp.GetRequiredService<DataPaths>(), applicationName, sp.GetRequiredService<ProblemCapture>()), preserveStaticLogger: true);
        return builder;
    }

    /// <summary>
    /// Adds <c>settings.json</c> from the data directory. It overrides the shipped appsettings.json defaults but
    /// not environment variables or command-line arguments. The data directory itself can only be changed through
    /// appsettings.json, environment variables or the command line, since it decides where settings.json lives.
    /// </summary>
    private static void AddUserSettingsFile(IConfigurationManager configuration)
    {
        var dataDirectory = configuration[$"{StorageOptions.SectionName}:{nameof(StorageOptions.DataDirectory)}"];
        var paths = new DataPaths(dataDirectory);
        Directory.CreateDirectory(paths.Root);

        var source = new ConfigurationBuilder()
            .AddJsonFile(paths.SettingsFile, optional: true, reloadOnChange: true)
            .Sources[0];

        var firstEnvironmentSource = -1;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is EnvironmentVariablesConfigurationSource)
            {
                firstEnvironmentSource = i;
                break;
            }
        }

        if (firstEnvironmentSource < 0)
        {
            configuration.Sources.Add(source);
        }
        else
        {
            configuration.Sources.Insert(firstEnvironmentSource, source);
        }
    }

    private static void AddValidatedOptions<TOptions, TValidator>(this IServiceCollection services, IConfiguration configuration, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().Bind(configuration.GetSection(section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
    }

    /// <summary>
    /// Loggers writing <c>logs\{applicationName}-*.log</c> like the hosts' (with problem reports), for a small helper
    /// process that runs without a host (the storage host).
    /// </summary>
    public static Microsoft.Extensions.Logging.ILoggerFactory CreateLoggerFactory(DataPaths paths, string applicationName)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var problems = new ProblemCapture(paths.ReportsDirectory, applicationName);
        problems.HookCrashes();
        var configuration = new LoggerConfiguration();
        ConfigureLogger(configuration, paths, applicationName, problems);
        return new Serilog.Extensions.Logging.SerilogLoggerFactory(configuration.CreateLogger(), dispose: true);
    }

    private static void ConfigureLogger(LoggerConfiguration logger, DataPaths paths, string applicationName, ProblemCapture problems)
    {
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("App", applicationName)
            .WriteTo.File(
                new CompactJsonFormatter(),
                Path.Combine(paths.LogsDirectory, $"{applicationName}-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedLogFiles)
            .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.Sink(problems);
    }
}
