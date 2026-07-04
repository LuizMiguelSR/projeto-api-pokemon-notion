using PokemonNotionApi.Options;
using PokemonNotionApi.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<NotionOptions>(builder.Configuration.GetSection(NotionOptions.SectionName));
builder.Services.Configure<LigaPokemonOptions>(builder.Configuration.GetSection(LigaPokemonOptions.SectionName));
builder.Services.AddHttpClient<NotionClientService>();
builder.Services.AddHttpClient<LigaPokemonScraperService>();
builder.Services.AddSingleton<CardPriceHistoryRepository>();
builder.Services.AddSingleton<BackgroundJobService>();
builder.Services.AddScoped<CardPriceChartService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedHost
        | ForwardedHeaders.XForwardedProto;

    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddRateLimiter(options =>
{
    var permitLimit = builder.Configuration.GetValue("RateLimiting:Public:PermitLimit", 60);
    var windowSeconds = builder.Configuration.GetValue("RateLimiting:Public:WindowSeconds", 60);

    options.AddPolicy("PublicEndpoints", httpContext =>
    {
        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(clientIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            message = "Too many requests. Try again later."
        }, cancellationToken);
    };
});
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/login";
        options.AccessDeniedPath = "/auth/denied";
        options.LogoutPath = "/auth/logout";
        options.Cookie.Name = "PokemonNotionApi.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    })
    .AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? string.Empty;
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AllowedUsers", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(context =>
        {
            var allowedEmails = builder.Configuration
                .GetSection("Authentication:AllowedEmails")
                .Get<string[]>() ?? [];

            if (allowedEmails.Length == 0)
            {
                return true;
            }

            var email = context.User.FindFirstValue(ClaimTypes.Email);
            return !string.IsNullOrWhiteSpace(email)
                && allowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase);
        });
    });
});

var app = builder.Build();

await app.Services.GetRequiredService<CardPriceHistoryRepository>().InitializeAsync(CancellationToken.None);

app.UseForwardedHeaders();
app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Redirect("/swagger"))
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "PokemonNotionApi",
    checkedAt = DateTimeOffset.UtcNow
})).RequireRateLimiting("PublicEndpoints");

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var path = context.Context.Request.Path.Value;
        if (path is not null &&
            (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
             path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)))
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

async Task<IResult> ServeHtmlAsync(string fileName, IWebHostEnvironment environment, CancellationToken cancellationToken)
{
    var html = await File.ReadAllTextAsync(Path.Combine(environment.WebRootPath, fileName), cancellationToken);
    return Results.Content(html, "text/html");
}

app.MapGet("/auth/login", (string? returnUrl) =>
{
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl },
        [GoogleDefaults.AuthenticationScheme]);
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/auth/logout", () =>
{
    return Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme]);
}).RequireAuthorization("AllowedUsers");

app.MapGet("/auth/me", (ClaimsPrincipal user) =>
{
    return Results.Ok(new
    {
        name = user.Identity?.Name,
        email = user.FindFirstValue(ClaimTypes.Email)
    });
}).RequireAuthorization("AllowedUsers");

app.MapGet("/auth/denied", async (IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    return await ServeHtmlAsync("access-denied.html", environment, cancellationToken);
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/Account/AccessDenied", () => Results.Redirect("/auth/denied"))
    .AllowAnonymous();

app.MapGet("/search", async (IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    return await ServeHtmlAsync("search.html", environment, cancellationToken);
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/refresh", async (IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    return await ServeHtmlAsync("refresh.html", environment, cancellationToken);
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/prices", async (IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    return await ServeHtmlAsync("prices.html", environment, cancellationToken);
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/api/sync/run", () => Results.Redirect("/refresh"))
    .RequireAuthorization("AllowedUsers");

app.MapGet("/api/sync/run/search", () => Results.Redirect("/search"))
    .RequireAuthorization("AllowedUsers");

async Task<IResult> RunSyncAsync(int? limit, SyncService syncService, CancellationToken cancellationToken)
{
    try
    {
        if (limit is <= 0)
        {
            return Results.BadRequest(new { error = "limit_must_be_positive" });
        }

        var result = await syncService.SyncDatabaseAsync(limit, cancellationToken);
        return Results.Ok(result);
    }
    catch (NotionApiException ex)
    {
        var syncLog = await syncService.CreateErrorLogAsync("sync_run", ex, cancellationToken);
        return Results.BadRequest(new
        {
            error = "notion_update_failed",
            notionStatusCode = ex.StatusCode,
            notionResponse = ex.ResponseBody,
            syncLog
        });
    }
    catch (Exception ex)
    {
        var syncLog = await syncService.CreateErrorLogAsync("sync_run", ex, cancellationToken);
        return Results.Problem(
            title: "Sync failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: new Dictionary<string, object?> { ["syncLog"] = syncLog });
    }
}

IResult RunSearchSyncAsync(BackgroundJobService jobs)
{
    var job = jobs.Start("sync-search", async (services, progress, cancellationToken) =>
    {
        var syncService = services.GetRequiredService<SyncService>();
        return await syncService.SyncDatabaseByLigaSearchAsync(progress, cancellationToken);
    });

    return Results.Accepted($"/api/jobs/{job.Id}", job);
}

async Task<IResult> RunPageSyncAsync(
    string pageId,
    SyncService syncService,
    CancellationToken cancellationToken)
{
    try
    {
        var result = await syncService.SyncPageByIdAsync(pageId, cancellationToken);
        return Results.Ok(result);
    }
    catch (NotionApiException ex)
    {
        var syncLog = await syncService.CreateErrorLogAsync($"card_sync:{pageId}", ex, cancellationToken);
        return Results.BadRequest(new
        {
            error = "notion_update_failed",
            notionStatusCode = ex.StatusCode,
            notionResponse = ex.ResponseBody,
            syncLog
        });
    }
    catch (Exception ex)
    {
        var syncLog = await syncService.CreateErrorLogAsync($"card_sync:{pageId}", ex, cancellationToken);
        var extensions = new Dictionary<string, object?> { ["syncLog"] = syncLog };
        if (ex is LigaPokemonScraperException scraperException)
        {
            extensions["ligaPokemon"] = new
            {
                scraperException.StatusCode,
                scraperException.ReasonPhrase,
                scraperException.SourceUrl,
                scraperException.ResponsePreview
            };
        }

        return Results.Problem(
            title: "Card sync failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError,
            extensions: extensions);
    }
}

IResult RefreshChartUrlsAsync(BackgroundJobService jobs)
{
    var job = jobs.Start("chart-urls-refresh", async (services, progress, cancellationToken) =>
    {
        var syncService = services.GetRequiredService<SyncService>();
        return await syncService.UpdateChartUrlsAsync(progress, cancellationToken);
    });

    return Results.Accepted($"/api/jobs/{job.Id}", job);
}

var api = app.MapGroup("/api").RequireAuthorization("AllowedUsers");

api.MapPost("/sync/run", RunSyncAsync);
api.MapPost("/sync/run/search", RunSearchSyncAsync);
api.MapPost("/cards/{pageId}/sync", RunPageSyncAsync);
api.MapPost("/cards/chart-urls/refresh", RefreshChartUrlsAsync);
api.MapPost("/jobs/sync-search", (BackgroundJobService jobs) =>
{
    var job = jobs.Start("sync-search", async (services, progress, cancellationToken) =>
    {
        var syncService = services.GetRequiredService<SyncService>();
        return await syncService.SyncDatabaseByLigaSearchAsync(progress, cancellationToken);
    });

    return Results.Accepted($"/api/jobs/{job.Id}", job);
});

api.MapPost("/jobs/chart-urls-refresh", (BackgroundJobService jobs) =>
{
    var job = jobs.Start("chart-urls-refresh", async (services, progress, cancellationToken) =>
    {
        var syncService = services.GetRequiredService<SyncService>();
        return await syncService.UpdateChartUrlsAsync(progress, cancellationToken);
    });

    return Results.Accepted($"/api/jobs/{job.Id}", job);
});

api.MapGet("/jobs/{jobId}", (string jobId, BackgroundJobService jobs) =>
{
    var job = jobs.Get(jobId);
    return job is null ? Results.NotFound(new { error = "job_not_found" }) : Results.Ok(job);
});

api.MapGet("/cards/{pageId}/prices", async (
    string pageId,
    int? limit,
    CardPriceChartService chartService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await chartService.GetChartDataAsync(pageId, limit ?? 120, cancellationToken));
});

app.MapGet("/cards/{pageId}/prices/data", async (
    string pageId,
    int? limit,
    CardPriceChartService chartService,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await chartService.GetChartDataAsync(pageId, limit ?? 120, cancellationToken));
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.MapGet("/cards/{pageId}/prices", async (IWebHostEnvironment environment, CancellationToken cancellationToken) =>
{
    var html = await File.ReadAllTextAsync(Path.Combine(environment.WebRootPath, "price-chart.html"), cancellationToken);
    return Results.Content(html, "text/html");
}).AllowAnonymous()
    .RequireRateLimiting("PublicEndpoints");

app.Run();

