namespace OrderPulse.Domain.Enums;

public enum OrderStatus
{
    Placed,
    PartiallyShipped,
    Shipped,
    InTransit,
    OutForDelivery,
    PartiallyDelivered,
    Delivered,
    DeliveryException,
    ReturnInProgress,
    ReturnReceived,
    Refunded,
    Cancelled,
    PartiallyCancelled,
    Closed,
    Inferred
}
