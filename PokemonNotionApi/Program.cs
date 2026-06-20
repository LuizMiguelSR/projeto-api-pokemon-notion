using PokemonNotionApi.Options;
using PokemonNotionApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<NotionOptions>(builder.Configuration.GetSection(NotionOptions.SectionName));
builder.Services.Configure<LigaPokemonOptions>(builder.Configuration.GetSection(LigaPokemonOptions.SectionName));
builder.Services.AddHttpClient<NotionClientService>();
builder.Services.AddHttpClient<LigaPokemonScraperService>();
builder.Services.AddSingleton<CardPriceHistoryRepository>();
builder.Services.AddScoped<CardPriceChartService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

await app.Services.GetRequiredService<CardPriceHistoryRepository>().InitializeAsync(CancellationToken.None);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Ok(new { service = "PokemonNotionApi", status = "running" }));

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "PokemonNotionApi",
    checkedAt = DateTimeOffset.UtcNow
}));

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/sync/run", async (SyncService syncService, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await syncService.SyncDatabaseAsync(cancellationToken);
        return Results.Ok(result);
    }
    catch (NotionApiException ex)
    {
        return Results.BadRequest(new
        {
            error = "notion_update_failed",
            notionStatusCode = ex.StatusCode,
            notionResponse = ex.ResponseBody
        });
    }
});

app.MapPost("/api/sync/run/search", async (SyncService syncService, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await syncService.SyncDatabaseByLigaSearchAsync(cancellationToken);
        return Results.Ok(result);
    }
    catch (NotionApiException ex)
    {
        return Results.BadRequest(new
        {
            error = "notion_update_failed",
            notionStatusCode = ex.StatusCode,
            notionResponse = ex.ResponseBody
        });
    }
});

app.MapPost("/api/cards/{pageId}/sync", async (
    string pageId,
    SyncService syncService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await syncService.SyncPageByIdAsync(pageId, cancellationToken);
        return Results.Ok(result);
    }
    catch (NotionApiException ex)
    {
        return Results.BadRequest(new
        {
            error = "notion_update_failed",
            notionStatusCode = ex.StatusCode,
            notionResponse = ex.ResponseBody
        });
    }
});

app.MapGet("/api/cards/{pageId}/prices", async (
    string pageId,
    int? limit,
    CardPriceChartService chartService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await chartService.GetChartDataAsync(pageId, limit ?? 120, cancellationToken));
});

app.MapGet("/cards/{pageId}/prices", async (IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    var html = await File.ReadAllTextAsync(Path.Combine(environment.WebRootPath, "price-chart.html"), cancellationToken);
    return Results.Content(html, "text/html");
});

app.Run();

