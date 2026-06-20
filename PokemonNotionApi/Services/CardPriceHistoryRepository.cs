using Microsoft.Extensions.Options;
using MySqlConnector;
using PokemonNotionApi.Models;
using PokemonNotionApi.Options;

namespace PokemonNotionApi.Services;

public sealed class CardPriceHistoryRepository(IOptions<DatabaseOptions> options)
{
    private readonly string _connectionString = options.Value.ConnectionString;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await CreateSchemaAsync(cancellationToken);
                return;
            }
            catch (MySqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    private async Task CreateSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS card_price_history (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                page_id VARCHAR(64) NOT NULL,
                card_name VARCHAR(255) NULL,
                card_number VARCHAR(64) NULL,
                source_url TEXT NOT NULL,
                image_url TEXT NULL,
                normal_price DECIMAL(12,2) NULL,
                foil_price DECIMAL(12,2) NULL,
                reverse_foil_price DECIMAL(12,2) NULL,
                captured_at DATETIME(6) NOT NULL,
                INDEX ix_card_price_history_page_captured (page_id, captured_at)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SaveAsync(CardPriceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return false;
        }

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO card_price_history
                (page_id, card_name, card_number, source_url, image_url, normal_price, foil_price, reverse_foil_price, captured_at)
            VALUES
                (@page_id, @card_name, @card_number, @source_url, @image_url, @normal_price, @foil_price, @reverse_foil_price, @captured_at);
            """;

        command.Parameters.AddWithValue("@page_id", snapshot.PageId);
        command.Parameters.AddWithValue("@card_name", (object?)snapshot.CardName ?? DBNull.Value);
        command.Parameters.AddWithValue("@card_number", (object?)snapshot.CardNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@source_url", snapshot.SourceUrl);
        command.Parameters.AddWithValue("@image_url", (object?)snapshot.ImageUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@normal_price", (object?)snapshot.NormalPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@foil_price", (object?)snapshot.FoilPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@reverse_foil_price", (object?)snapshot.ReverseFoilPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@captured_at", snapshot.CapturedAt.UtcDateTime);

        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CardPriceSnapshot>> GetHistoryAsync(
        string pageId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 500);

        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT page_id, card_name, card_number, source_url, image_url,
                   normal_price, foil_price, reverse_foil_price, captured_at
            FROM card_price_history
            WHERE page_id = @page_id
            ORDER BY captured_at DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@page_id", pageId);
        command.Parameters.AddWithValue("@limit", limit);

        var snapshots = new List<CardPriceSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            snapshots.Add(ReadSnapshot(reader));
        }

        snapshots.Reverse();
        return snapshots;
    }

    private static CardPriceSnapshot ReadSnapshot(MySqlDataReader reader)
    {
        return new CardPriceSnapshot
        {
            PageId = reader.GetString("page_id"),
            CardName = reader.IsDBNull(reader.GetOrdinal("card_name")) ? null : reader.GetString("card_name"),
            CardNumber = reader.IsDBNull(reader.GetOrdinal("card_number")) ? null : reader.GetString("card_number"),
            SourceUrl = reader.GetString("source_url"),
            ImageUrl = reader.IsDBNull(reader.GetOrdinal("image_url")) ? null : reader.GetString("image_url"),
            NormalPrice = GetNullableDecimal(reader, "normal_price"),
            FoilPrice = GetNullableDecimal(reader, "foil_price"),
            ReverseFoilPrice = GetNullableDecimal(reader, "reverse_foil_price"),
            CapturedAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime("captured_at"), DateTimeKind.Utc))
        };
    }

    private static decimal? GetNullableDecimal(MySqlDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);
    }
}
