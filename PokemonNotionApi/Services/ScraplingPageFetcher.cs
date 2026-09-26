using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PokemonNotionApi.Options;

namespace PokemonNotionApi.Services;

public sealed class ScraplingPageFetcher(IOptions<LigaPokemonOptions> options)
{
    public async Task<(int StatusCode, string Html)> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ScraplingTimeoutSeconds));
        var start = new ProcessStartInfo(settings.ScraplingPythonExecutable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Scraping", "liga_pokemon.py"));
        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                "Cannot start Scrapling. Install its Python environment and configure LigaPokemon:ScraplingPythonExecutable.", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            var input = JsonSerializer.Serialize(new
            {
                url,
                baseUrl = settings.BaseUrl,
                cookie = settings.Cookie,
                acceptLanguage = settings.AcceptLanguage,
                timeoutSeconds = settings.ScraplingTimeoutSeconds
            });
            await process.StandardInput.WriteLineAsync(input.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            await errorTask; // Drain diagnostics without exposing cookies or page content.
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Scrapling failed to load the card page. Check Python dependencies, installed Chromium and network access.");

            using var result = JsonDocument.Parse(output);
            return (result.RootElement.GetProperty("statusCode").GetInt32(),
                result.RootElement.GetProperty("html").GetString() ?? "");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Scrapling timed out while loading the card page.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(outputTask, errorTask);
        }
    }
}
