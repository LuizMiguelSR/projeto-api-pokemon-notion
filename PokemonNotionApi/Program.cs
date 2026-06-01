using PokemonNotionApi.Options;
using PokemonNotionApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<NotionOptions>(builder.Configuration.GetSection(NotionOptions.SectionName));
builder.Services.Configure<LigaPokemonOptions>(builder.Configuration.GetSection(LigaPokemonOptions.SectionName));
builder.Services.AddHttpClient<NotionClientService>();
builder.Services.AddHttpClient<LigaPokemonScraperService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Ok(new { service = "PokemonNotionApi", status = "running" }));

app.MapPost("/api/sync/run", async (SyncService syncService, CancellationToken cancellationToken) =>
{
    var result = await syncService.SyncDatabaseAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/sync/page/{pageId}", async (string pageId, SyncService syncService, CancellationToken cancellationToken) =>
{
    var result = await syncService.SyncSinglePageAsync(pageId, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.Run();
