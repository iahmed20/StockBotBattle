// Models/Order.cs
public class Order
{
    public long OrderId { get; set; }
    public int AccountId { get; set; }
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";       // "BUY" or "SELL"
    public string OrderType { get; set; } = "";  // "MARKET" or "LIMIT"
    public decimal? LimitPrice { get; set; }      // null for market orders
    public decimal Quantity { get; set; }
    public decimal QuantityFilled { get; set; } = 0;
    public string Status { get; set; } = OrderStatus.Open;
    public string? StatusReason { get; set; }     // why an order was rejected or cancelled
    public int? StrategySubmissionId { get; set; } // set when a bot placed the order
    public int? RoundId { get; set; }              // the round's order book; null for the open market
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public decimal RemainingQty => Quantity - QuantityFilled;
}

public static class OrderStatus
{
    public const string Open = "OPEN";           // resting on the book, nothing filled yet
    public const string Partial = "PARTIAL";     // resting on the book, partly filled
    public const string Filled = "FILLED";
    public const string Cancelled = "CANCELLED"; // may be partly filled; see QuantityFilled
    public const string Rejected = "REJECTED";

    public static readonly string[] Working = { Open, Partial };
}
