﻿using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.Utils;
using IdentityModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace SamplesIntegrationTests;

public static class DistributedApplicationExtensions
{
    public static TBuilder WriteOutputTo<TBuilder>(this TBuilder builder, ITestOutputHelper testOutputHelper, bool throwOnError = false)
        where TBuilder : IDistributedApplicationTestingBuilder
    {
        var xunitLoggerProvider = new XUnitLoggerProvider(testOutputHelper, throwOnError);
        builder.Services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.AddProvider(xunitLoggerProvider);
        });

        builder.Services.AddHostedService(sp => new ResourceWatcher(
            sp.GetRequiredService<ResourceNotificationService>(),
            sp.GetRequiredService<ResourceLoggerService>(),
            testOutputHelper));

        return builder;
    }

    public static TBuilder WithRandomParameterValues<TBuilder>(this TBuilder builder)
        where TBuilder : IDistributedApplicationTestingBuilder
    {
        var parameters = builder.Resources.OfType<ParameterResource>().Where(p => !p.IsConnectionString).ToList();
        foreach (var parameter in parameters)
        {
            builder.Configuration[$"Parameters:{parameter.Name}"] = parameter.Secret
                ? PasswordGenerator.Generate(16, true, true, true, false, 1, 1, 1, 0)
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        }

        return builder;
    }

    public static TBuilder WithAnonymousVolumeNames<TBuilder>(this TBuilder builder)
        where TBuilder : IDistributedApplicationTestingBuilder
    {
        foreach (var resource in builder.Resources)
        {
            if (resource.TryGetAnnotationsOfType<ContainerMountAnnotation>(out var mounts))
            {
                var mountsList = mounts.ToList();

                for (var i = 0; i < mountsList.Count; i++)
                {
                    var mount = mountsList[i];
                    if (mount.Type == ContainerMountType.Volume)
                    {
                        var newMount = new ContainerMountAnnotation(null, mount.Target, mount.Type, mount.IsReadOnly);
                        resource.Annotations.Remove(mount);
                        resource.Annotations.Add(newMount);
                    }
                }
            }
        }

        return builder;
    }

    public static async Task StartAsync(this DistributedApplication app, bool waitForResourcesToStart, CancellationToken cancellationToken = default)
    {
        await app.StartAsync(cancellationToken);

        if (waitForResourcesToStart)
        {
            var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WaitForResources");
            var resources = GetWaitableResources(app).ToList();
            var remainingResources = new ConcurrentDictionary<string, int>(resources.Select(r => KeyValuePair.Create(r.Name, 0)), StringComparer.OrdinalIgnoreCase);

            logger.LogInformation("Waiting on {resourcesToStartCount} resources to start", remainingResources.Count);

            await Task.WhenAll(resources.Select(async r =>
            {
                await app.WaitForResourceToStartAsync(r.Name, logger, cancellationToken);
                remainingResources.TryRemove(r.Name, out var _);
                logger.LogInformation("Resource '{resourceName}' started", r.Name);
                var names = remainingResources.Keys;
                if (names.Count > 0)
                {
                    var resourceNames = string.Join(", ", names);
                    logger.LogInformation("Still waiting on {resourcesToStartCount} resources to start: {resourcesToStart}", names.Count, resourceNames);
                }
            }));
            
            logger.LogInformation("All resources started successfully");
        }
    }

    private static IEnumerable<IResource> GetWaitableResources(this DistributedApplication app)
    {
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        return appModel.Resources.Where(r =>
            r.GetType().IsAssignableTo(typeof(ContainerResource))
            || r.GetType().IsAssignableTo(typeof(ExecutableResource))
            || r.GetType().IsAssignableTo(typeof(ProjectResource))
            || r.GetType().IsAssignableTo(typeof(AzureConstructResource))
            );
    }

    private static readonly TimeSpan resourceStartTimeout = TimeSpan.FromSeconds(30);

    public static async Task WaitForResourceToStartAsync(this DistributedApplication app, string resourceName, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        var notificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.FirstOrDefault(r => string.Equals(r.Name, resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Resource with name '{resourceName}' was not found", nameof(resourceName));

        var timeoutCts = new CancellationTokenSource(resourceStartTimeout);
        var cts = cancellationToken == default
            ? timeoutCts
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await foreach (var resourceEvent in notificationService.WatchAsync().WithCancellation(cts.Token))
        {
            if (string.Equals(resourceEvent.Resource.Name, resourceName, StringComparison.Ordinal))
            {
                var snapshot = resourceEvent.Snapshot;
                if (snapshot.ExitCode is { } exitCode)
                {
                    if (exitCode != 0)
                    {
                        // Error starting resource
                        throw new DistributedApplicationException($"Resource '{resourceName}' exited with exit code {exitCode}");
                    }

                    // Resource exited cleanly
                    return;
                }
                else if (snapshot.State is { } state)
                {
                    if (state.Text.Contains("running", StringComparison.OrdinalIgnoreCase)
                        || state.Text.Contains("exited", StringComparison.OrdinalIgnoreCase))
                    {
                        // Resource started or exited
                        return;
                    }
                    else if (state.Text.Contains("failedtostart", StringComparison.OrdinalIgnoreCase))
                    {
                        // Resource failed to start
                        throw new DistributedApplicationException($"Resource '{resourceName}' failed to start: {state.Text}");
                    }
                    else if (state.Text.Contains("starting", StringComparison.OrdinalIgnoreCase)
                             || state.Text.Contains("hidden", StringComparison.OrdinalIgnoreCase))
                    {
                        // Resource is still starting
                        continue;
                    }
                    else if (!string.IsNullOrEmpty(state.Text))
                    {
                        logger?.LogWarning("Unknown resource state encountered: {state}", state.Text);
                    }
                }
            }
        }
    }

    private class ResourceWatcher(
        ResourceNotificationService resourceNotification,
        ResourceLoggerService resourceLoggerService,
        ITestOutputHelper testOutputHelper)
        : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return WatchNotifications(stoppingToken);
        }

        private async Task WatchNotifications(CancellationToken stoppingToken)
        {
            var loggingResources = new HashSet<string>();
            var logWatchTasks = new List<Task>();

            await foreach (var resourceEvent in resourceNotification.WatchAsync().WithCancellation(stoppingToken))
            {
                if (loggingResources.Add(resourceEvent.ResourceId))
                {
                    logWatchTasks.Add(WatchLogs(resourceEvent.Resource.Name, resourceEvent.ResourceId, stoppingToken));
                }
                if (resourceEvent.Snapshot.ExitCode is null && resourceEvent.Snapshot.State is { } state && !string.IsNullOrEmpty(state.Text))
                {
                    testOutputHelper.WriteLine("Resource '{0}' of type '{1}' -> {2}", resourceEvent.ResourceId, resourceEvent.Resource.GetType().Name, state.Text);
                }
                else if (resourceEvent.Snapshot.ExitCode is { } exitCode)
                {
                    testOutputHelper.WriteLine("Resource '{0}' exited with code {1}", resourceEvent.ResourceId, exitCode);
                }
            }

            await Task.WhenAll(logWatchTasks);
        }

        private async Task WatchLogs(string resourceName, string resourceId, CancellationToken stoppingToken)
        {
            await foreach (var logEvent in resourceLoggerService.WatchAsync(resourceId).WithCancellation(stoppingToken))
            {
                foreach (var line in logEvent)
                {
                    var kind = line.IsErrorMessage ? "error" : "log";
                    testOutputHelper.WriteLine("Resource '{0}' {1}: {2}", resourceName, kind, line.Content);
                }
            }
        }
    }

    private class XUnitLogger(ITestOutputHelper testOutputHelper, LoggerExternalScopeProvider scopeProvider, string categoryName, bool throwOnError = false) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => scopeProvider.Push(state);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var sb = new StringBuilder();

            sb.Append(GetLogLevelString(logLevel))
              .Append(" [").Append(categoryName).Append("] ")
              .Append(formatter(state, exception));

            if (exception is not null)
            {
                sb.Append('\n').Append(exception);
            }

            // Append scopes
            scopeProvider.ForEachScope((scope, state) =>
            {
                state.Append("\n => ");
                state.Append(scope);
            }, sb);

            testOutputHelper.WriteLine(sb.ToString());

            if (throwOnError && (logLevel == LogLevel.Error || logLevel == LogLevel.Critical))
            {
                var message = $"An error occurred: {formatter(state, exception)}";
                if (exception is null)
                {
                    throw new DistributedApplicationException(message);
                }
                else
                {
                    throw new DistributedApplicationException(message, exception);
                }
            }
        }

        private static string GetLogLevelString(LogLevel logLevel)
        {
            return logLevel switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => throw new ArgumentOutOfRangeException(nameof(logLevel))
            };
        }
    }

    private sealed class XUnitLogger<T>(ITestOutputHelper testOutputHelper, LoggerExternalScopeProvider scopeProvider)
        : XUnitLogger(testOutputHelper, scopeProvider, typeof(T).FullName ?? ""), ILogger<T>
    {
    }

    private sealed class XUnitLoggerProvider(ITestOutputHelper testOutputHelper, bool throwOnError = false) : ILoggerProvider
    {
        private readonly LoggerExternalScopeProvider _scopeProvider = new();

        public ILogger CreateLogger(string categoryName)
        {
            return new XUnitLogger(testOutputHelper, _scopeProvider, categoryName, throwOnError);
        }

        public void Dispose()
        {
        }
    }
}
