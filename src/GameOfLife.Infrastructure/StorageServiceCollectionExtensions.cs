using GameOfLife.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace GameOfLife.Infrastructure;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddBoardStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .PostConfigure<IHostEnvironment>((options, environment) =>
                // Without this, the data would land wherever the process happened to be started from.
                options.Path = Path.GetFullPath(options.Path, environment.ContentRootPath))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<LmdbBoardStore>();
        services.AddSingleton<IBoardStore>(provider => new CachedBoardStore(
            provider.GetRequiredService<LmdbBoardStore>(),
            provider.GetRequiredService<IOptions<StorageOptions>>().Value.CacheSizeLimitBytes));

        services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage");
        return services;
    }

    /// <summary>Opens the store at startup, so a bad path or locked file fails fast instead of on the first request.</summary>
    public static void InitializeBoardStorage(this IServiceProvider services) =>
        services.GetRequiredService<LmdbBoardStore>();

    private sealed class StorageHealthCheck(LmdbBoardStore store) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                store.Ping();
                return Task.FromResult(HealthCheckResult.Healthy());
            }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy("Board storage is unavailable.", ex));
            }
        }
    }
}
