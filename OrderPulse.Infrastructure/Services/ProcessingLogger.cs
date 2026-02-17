using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace OrderPulse.Infrastructure.Services;

/// <summary>
/// Writes diagnostic entries to the ProcessingLog table.
/// Uses raw SQL (no EF, no RLS) so logs are always visible.
/// </summary>
public class ProcessingLogger
{
    private readonly string _connectionString;

    public ProcessingLogger(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("OrderPulseDb")
            ?? throw new InvalidOperationException("OrderPulseDb connection string not configured");
    }

    public async Task LogAsync(Guid? emailMessageId, string step, string status, string message, string? details = null)
    {
        try
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ProcessingLog (EmailMessageId, Step, Status, Message, Details)
                VALUES (@EmailMessageId, @Step, @Status, @Message, @Details)";
            cmd.Parameters.AddWithValue("@EmailMessageId", (object?)emailMessageId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Step", step);
            cmd.Parameters.AddWithValue("@Status", status);
            cmd.Parameters.AddWithValue("@Message", message);
            cmd.Parameters.AddWithValue("@Details", (object?)details ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Never let logging break the pipeline
        }
    }

    public Task Info(Guid? id, string step, string message, string? details = null)
        => LogAsync(id, step, "Info", message, details);

    public Task Success(Guid? id, string step, string message, string? details = null)
        => LogAsync(id, step, "Success", message, details);

    public Task Warn(Guid? id, string step, string message, string? details = null)
        => LogAsync(id, step, "Warning", message, details);

    public Task Error(Guid? id, string step, string message, string? details = null)
        => LogAsync(id, step, "Error", message, details);
}
