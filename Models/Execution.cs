// Models/Execution.cs
// One side of a fill. A trade between two orders writes one execution per order;
// a trade against the house market maker writes one, with PriceTickId set.
public class Execution
{
    public long ExecutionId { get; set; }
    public long OrderId { get; set; }
    public long? CounterOrderId { get; set; } // null when the house was the counterparty
    public long? PriceTickId { get; set; }    // the house quote this filled against
    public string Symbol { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;
}
