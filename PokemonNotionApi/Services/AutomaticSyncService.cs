using Microsoft.Extensions.Options;
using PokemonNotionApi.Options;

namespace PokemonNotionApi.Services;

public sealed class AutomaticSyncService(
    IServiceScopeFactory scopeFactory,
    IOptions<AutomaticSyncOptions> options,
    ILogger<AutomaticSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Automatic card price synchronization is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(settings.IntervalHours));
        try
        {
            // Run at startup, then on each interval. Await each run to avoid overlap.
            do
            {
                try
                {
                    logger.LogInformation("Starting automatic card price synchronization.");
                    using var scope = scopeFactory.CreateScope();
                    var sync = scope.ServiceProvider.GetRequiredService<SyncService>();
                    var result = await sync.SyncDatabaseAsync(null, stoppingToken);
                    logger.LogInformation("Automatic card price synchronization finished: {@Result}", result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "Automatic card price synchronization failed. The next scheduled run will still execute.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown: cancellation also stops an active Scrapling process.
        }
    }
}
