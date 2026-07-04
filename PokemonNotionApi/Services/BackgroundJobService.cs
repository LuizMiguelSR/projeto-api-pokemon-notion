using System.Collections.Concurrent;

namespace PokemonNotionApi.Services;

public sealed class BackgroundJobService(IServiceScopeFactory scopeFactory, ILogger<BackgroundJobService> logger)
{
    private readonly ConcurrentDictionary<string, BackgroundJobStatus> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public BackgroundJobStatus Start(string name, Func<IServiceProvider, IProgress<JobProgress>, CancellationToken, Task<object>> work)
    {
        var job = new BackgroundJobStatus
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            State = "running",
            StartedAt = DateTimeOffset.UtcNow
        };

        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var progress = new Progress<JobProgress>(value =>
                {
                    job.Processed = value.Processed;
                    job.Total = value.Total;
                    job.Message = value.Message;
                });
                var result = await work(scope.ServiceProvider, progress, CancellationToken.None);
                job.State = "completed";
                job.Result = result;
                job.Processed = job.Total ?? job.Processed;
                job.Message = "Processamento finalizado.";
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job {JobId} failed.", job.Id);
                job.State = "failed";
                job.Message = "O job terminou com erro.";
                job.Error = new
                {
                    type = ex.GetType().Name,
                    message = ex.Message
                };
            }
            finally
            {
                job.FinishedAt = DateTimeOffset.UtcNow;
            }
        });

        return job;
    }

    public BackgroundJobStatus? Get(string id)
    {
        return _jobs.TryGetValue(id, out var job) ? job : null;
    }
}

public sealed class BackgroundJobStatus
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string State { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? Processed { get; set; }
    public int? Total { get; set; }
    public string? Message { get; set; }
    public int? Percent => Total is > 0 && Processed.HasValue
        ? Math.Clamp((int)Math.Round((double)Processed.Value / Total.Value * 100), 0, 100)
        : null;
    public object? Result { get; set; }
    public object? Error { get; set; }
}

public sealed record JobProgress(int Processed, int Total, string? Message = null);
