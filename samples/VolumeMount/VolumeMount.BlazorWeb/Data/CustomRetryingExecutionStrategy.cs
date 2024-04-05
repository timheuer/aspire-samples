﻿using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace VolumeMount.BlazorWeb.Data;

public class CustomRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies) : SqlServerRetryingExecutionStrategy(dependencies)
{
    protected override bool ShouldRetryOn(Exception exception)
    {
        if (exception is SqlException sqlException)
        {
            foreach (SqlError error in sqlException.Errors)
            {
                // EF Core issue logged to consider making this a default https://github.com/dotnet/efcore/issues/33191
                if (error.Number == 4060)
                {
                    // Don't retry on login failures associated with default database not existing due to EF migrations not running yet
                    return false;
                }
                // Workaround for https://github.com/dotnet/aspire/issues/1023
                else if (error.Number == 203 && sqlException.InnerException is Win32Exception)
                {
                    return true;
                }
            }
        }

        return base.ShouldRetryOn(exception);
    }
}
