namespace OrderPulse.Domain.Enums;

public enum EmailClassificationType
{
    OrderConfirmation,
    OrderModification,
    OrderCancellation,
    PaymentConfirmation,
    ShipmentConfirmation,
    ShipmentUpdate,
    DeliveryConfirmation,
    DeliveryIssue,
    ReturnInitiation,
    ReturnLabel,
    ReturnReceived,
    ReturnRejection,
    RefundConfirmation,
    Promotional
}
