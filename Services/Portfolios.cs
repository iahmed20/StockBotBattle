// Services/Portfolios.cs
using Microsoft.EntityFrameworkCore;

public record PositionView(string Symbol, decimal Quantity, decimal Available);
public record PortfolioView(int AccountId, decimal CashBalance, decimal AvailableCash, List<PositionView> Positions);

public static class Portfolios
{
    // Cash and positions, including what is reserved by working orders
    public static async Task<PortfolioView> Load(BrokerageContext db, MatchingEngine engine, int accountId)
    {
        var balances = await engine.GetBalances(accountId);

        var working = OrderStatus.Working;
        var held = await db.LedgerEntries
            .Where(l => l.AccountId == accountId && l.EntryType == "POSITION")
            .GroupBy(l => l.Symbol!)
            .Select(g => new { Symbol = g.Key, Quantity = g.Sum(l => l.Amount) })
            .ToListAsync();
        var reserved = await db.Orders
            .Where(o => o.AccountId == accountId && o.Side == "SELL" && working.Contains(o.Status))
            .GroupBy(o => o.Symbol)
            .Select(g => new { Symbol = g.Key, Quantity = g.Sum(o => o.Quantity - o.QuantityFilled) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Quantity);

        var positions = held
            .Where(p => p.Quantity != 0)
            .OrderBy(p => p.Symbol)
            .Select(p => new PositionView(p.Symbol, p.Quantity, p.Quantity - reserved.GetValueOrDefault(p.Symbol)))
            .ToList();

        return new PortfolioView(accountId, balances.Cash, balances.AvailableCash, positions);
    }
}
