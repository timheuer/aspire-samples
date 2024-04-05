﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using Polly;
using Polly.Retry;

namespace DatabaseMigrations.MigrationService;

public class ApiDbInitializer(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime hostApplicationLifetime) : BackgroundService
{
    public const string ActivitySourceName = "Migrations";
    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var activity = s_activitySource.StartActivity("Migrating database", ActivityKind.Client);

        try
        {
            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MyDb1Context>();

            await EnsureDatabaseAsync(dbContext, cancellationToken);
            await RunMigrationAsync(dbContext, cancellationToken);
        }
        catch (Exception ex)
        {
            activity?.RecordException(ex);
            throw;
        }

        hostApplicationLifetime.StopApplication();
    }

    private static async Task EnsureDatabaseAsync(MyDb1Context dbContext, CancellationToken cancellationToken)
    {
        var dbCreator = dbContext.GetService<IRelationalDatabaseCreator>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Workround for https://github.com/dotnet/aspire/issues/1023
            var retry = new ResiliencePipelineBuilder()
                .AddRetry(new RetryStrategyOptions
                    {
                        ShouldHandle = new PredicateBuilder().Handle<SqlException>(ex =>
                            ex.Number is 203 && ex.InnerException is Win32Exception),
                    })
                .AddTimeout(TimeSpan.FromSeconds(120))
                .Build();

            await retry.ExecuteAsync(static async (db, token) =>
            {
                // Create the database if it does not exist.
                // Do this first so there is then a database to start a transaction against.
                if (!await db.ExistsAsync(token))
                {
                    await db.CreateAsync(token);
                }
            }, dbCreator, cancellationToken);
        });
    }

    private static async Task RunMigrationAsync(MyDb1Context dbContext, CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // Run migration in a transaction to avoid partial migration if it fails.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await dbContext.Database.MigrateAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }
}
