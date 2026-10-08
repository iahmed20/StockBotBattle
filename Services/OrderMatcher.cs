// Services/OrderMatcher.cs
// Pure matching logic, kept free of the database so it can be unit tested.

// Liquidity an incoming order can trade against: a resting order, or the house quote (OrderId null)
public record Liquidity(long? OrderId, decimal Price, decimal Quantity);

// A fill against one piece of liquidity; RestingOrderId is null for the house
public record Fill(long? RestingOrderId, decimal Price, decimal Quantity);

public static class OrderMatcher
{
    // Matches an incoming order against the opposite side of the book.
    // `book` holds resting orders in time order (oldest first) plus the house quote, in any order.
    // Priority is best price first; at equal prices resting orders go before the house, oldest first.
    // A limit order only takes liquidity at its limit or better. `cashLimit` caps what a buy can spend.
    public static List<Fill> Match(string side, decimal? limitPrice, decimal quantity,
        IEnumerable<Liquidity> book, decimal? cashLimit = null)
    {
        var isBuy = side == "BUY";
        var ranked = book
            .Select((l, arrival) => (l, arrival))
            .Where(x => x.l.Quantity > 0)
            .Where(x => limitPrice == null || (isBuy ? x.l.Price <= limitPrice : x.l.Price >= limitPrice))
            .OrderBy(x => isBuy ? x.l.Price : -x.l.Price)
            .ThenBy(x => x.l.OrderId == null ? 1 : 0)
            .ThenBy(x => x.arrival)
            .Select(x => x.l);

        var fills = new List<Fill>();
        var remaining = quantity;
        var cash = cashLimit;

        foreach (var level in ranked)
        {
            if (remaining <= 0) break;

            var qty = Math.Min(remaining, level.Quantity);
            if (isBuy && cash != null)
                qty = Math.Min(qty, Math.Floor(cash.Value / level.Price)); // whole shares only
            if (qty <= 0) break; // can't afford even one share at this price, and later levels cost more

            fills.Add(new Fill(level.OrderId, level.Price, qty));
            remaining -= qty;
            if (cash != null) cash -= qty * level.Price;
        }

        return fills;
    }

    // Signed ledger amounts for one fill: (cash, position) from the point of view of the order's account
    public static (decimal Cash, decimal Position) LedgerDeltas(string side, decimal price, decimal quantity) =>
        side == "BUY" ? (-price * quantity, quantity) : (price * quantity, -quantity);
}
