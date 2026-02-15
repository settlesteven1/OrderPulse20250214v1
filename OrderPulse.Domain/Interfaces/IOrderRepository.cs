using OrderPulse.Domain.Entities;
using OrderPulse.Domain.Enums;

namespace OrderPulse.Domain.Interfaces;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid orderId, CancellationToken ct = default);
    Task<Order?> GetByExternalOrderNumberAsync(string externalOrderNumber, CancellationToken ct = default);
    Task<(IReadOnlyList<Order> Items, int TotalCount)> GetOrdersAsync(OrderQueryParameters query, CancellationToken ct = default);
    Task<Order> CreateAsync(Order order, CancellationToken ct = default);
    Task UpdateAsync(Order order, CancellationToken ct = default);
    Task<Dictionary<OrderStatus, int>> GetStatusCountsAsync(CancellationToken ct = default);
}

public class OrderQueryParameters
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public OrderStatus? Status { get; set; }
    public string? StatusShortcut { get; set; }  // "awaiting-delivery", "needs-attention", etc.
    public Guid? RetailerId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Search { get; set; }
    public string SortBy { get; set; } = "OrderDate";
    public bool SortDescending { get; set; } = true;
}

public interface IReturnRepository
{
    Task<Return?> GetByIdAsync(Guid returnId, CancellationToken ct = default);
    Task<(IReadOnlyList<Return> Items, int TotalCount)> GetReturnsAsync(ReturnQueryParameters query, CancellationToken ct = default);
    Task<IReadOnlyList<Return>> GetOpenReturnLabelsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Return>> GetAwaitingRefundAsync(CancellationToken ct = default);
    Task<Return> CreateAsync(Return returnEntity, CancellationToken ct = default);
    Task UpdateAsync(Return returnEntity, CancellationToken ct = default);
}

public class ReturnQueryParameters
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public ReturnStatus? Status { get; set; }
    public Guid? OrderId { get; set; }
}

public interface IEmailMessageRepository
{
    Task<EmailMessage?> GetByGraphMessageIdAsync(string graphMessageId, CancellationToken ct = default);
    Task<IReadOnlyList<EmailMessage>> GetPendingAsync(int batchSize = 50, CancellationToken ct = default);
    Task<(IReadOnlyList<EmailMessage> Items, int TotalCount)> GetReviewQueueAsync(int page, int pageSize, CancellationToken ct = default);
    Task<EmailMessage> CreateAsync(EmailMessage email, CancellationToken ct = default);
    Task UpdateAsync(EmailMessage email, CancellationToken ct = default);
}
