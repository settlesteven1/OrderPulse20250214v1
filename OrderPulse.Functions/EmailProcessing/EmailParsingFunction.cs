using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderPulse.Domain.Interfaces;
using OrderPulse.Infrastructure.Data;

namespace OrderPulse.Functions.EmailProcessing;

/// <summary>
/// Service Bus-triggered function that parses classified emails and creates
/// order/shipment/return/refund records via the processing orchestrator.
/// Picks up from 'emails-classified' queue after EmailClassifierFunction.
/// </summary>
public class EmailParsingFunction
{
    private readonly ILogger<EmailParsingFunction> _logger;
    private readonly OrderPulseDbContext _db;
    private readonly IEmailProcessingOrchestrator _orchestrator;

    public EmailParsingFunction(
        ILogger<EmailParsingFunction> logger,
        OrderPulseDbContext db,
        IEmailProcessingOrchestrator orchestrator)
    {
        _logger = logger;
        _db = db;
        _orchestrator = orchestrator;
    }

    [Function("EmailParsingFunction")]
    public async Task Run(
        [ServiceBusTrigger("emails-classified", Connection = "ServiceBusConnection")]
        string emailMessageId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(emailMessageId, out var id))
        {
            _logger.LogError("Invalid email message ID: {id}", emailMessageId);
            return;
        }

        _logger.LogInformation("Parsing classified email {id}", id);

        // Look up the email to get tenant context
        var email = await _db.EmailMessages
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.EmailMessageId == id, ct);

        if (email is null)
        {
            _logger.LogWarning("Email {id} not found", id);
            return;
        }

        // Set tenant context so RLS allows DB reads/writes
        FunctionsTenantProvider.SetCurrentTenant(email.TenantId);
        await _db.Database.ExecuteSqlRawAsync(
            "EXEC sp_set_session_context @key=N'TenantId', @value={0}",
            email.TenantId.ToString());

        try
        {
            await _orchestrator.ProcessEmailAsync(id, ct);
            _logger.LogInformation("Successfully parsed email {id}", id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse email {id}", id);
            throw; // Let Service Bus handle retry/dead-letter
        }
    }
}
