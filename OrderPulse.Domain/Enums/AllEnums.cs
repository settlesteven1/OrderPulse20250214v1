namespace OrderPulse.Domain.Enums;

public enum OrderLineStatus
{
    Ordered,
    Shipped,
    Delivered,
    Cancelled,
    ReturnInitiated,
    Returned,
    Refunded
}

public enum ShipmentStatus
{
    Shipped,
    InTransit,
    OutForDelivery,
    Delivered,
    Exception,
    Returned
}

public enum DeliveryStatus
{
    Delivered,
    AttemptedDelivery,
    DeliveryException,
    Lost
}

public enum DeliveryIssueType
{
    Missing,
    Damaged,
    WrongItem,
    NotReceived,
    Stolen,
    Other
}

public enum ReturnStatus
{
    Initiated,
    LabelIssued,
    Shipped,
    Received,
    Rejected,
    RefundPending,
    Refunded,
    Closed
}

public enum ReturnMethod
{
    Mail,
    DropOff,
    Pickup
}

public enum MailboxProvider
{
    AzureExchange,
    Gmail,
    Other
}
