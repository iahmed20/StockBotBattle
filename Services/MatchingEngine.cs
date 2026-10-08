// Services/MatchingEngine.cs
using Microsoft.EntityFrameworkCore;

public class MarketOptions
{
    // Shares the house market maker will buy and sell at each price tick, per symbol and side
    public decimal HouseDepth { get; set; } = 1000;
}

public record PlaceOrderRequest(string Symbol, string Side, string OrderType, decimal? LimitPrice, decimal Quantity);

// A malformed order (unknown symbol, bad side, ...). Nothing is saved.
public class OrderValidationException : Exception
{
    public OrderValidationException(string message) : base(message) { }
}

public record Balances(decimal Cash, decimal ReservedCash, decimal Position, decimal ReservedShares)
{
    public decimal AvailableCash => Cash - ReservedCash;
    public decimal AvailableShares => Position - ReservedShares;
}

// Order book plus a house market maker. The house quotes both sides at the latest tick price,
// up to MarketOptions.HouseDepth shares per side per tick. Incoming orders take the best price
// available across resting orders and the house (price-time priority); limit orders that aren't
// fully filled rest on the book and are re-checked against the house on every tick.
//
// Locking: an order locks its account row, then its security row. The security lock serializes
// all matching for a symbol; the account lock stops two orders from spending the same cash.
// Cash and shares behind resting orders are reserved, so fills against another account's
// resting order never need that account's lock.
public class MatchingEngine
{
    private const decimal MaxQuantity = 1_000_000;

    private readonly BrokerageContext _db;
    private readonly MarketOptions _options;

    public MatchingEngine(BrokerageContext db, MarketOptions options)
    {
        _db = db;
        _options = options;
    }

    // Validates, checks funds, and matches an order. Business rejections (not enough cash or shares,
    // no price yet) are saved as REJECTED orders; malformed requests throw OrderValidationException.
    public async Task<Order> PlaceOrder(int accountId, PlaceOrderRequest request, int? strategySubmissionId = null)
    {
        var order = Normalize(request);
        order.AccountId = accountId;
        order.StrategySubmissionId = strategySubmissionId;

        await using var transaction = await _db.Database.BeginTransactionAsync();

        await LockAccount(accountId);
        if (!await LockSecurity(order.Symbol))
            throw new OrderValidationException($"Unknown symbol '{order.Symbol}'.");

        var tick = await LatestTick(order.Symbol);
        var balances = await GetBalances(accountId, order.Symbol);
        var rejection = CheckFunds(order, tick, balances);

        _db.Orders.Add(order);
        if (rejection != null)
        {
            order.Status = OrderStatus.Rejected;
            order.StatusReason = rejection;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return order;
        }
        await _db.SaveChangesAsync(); // assigns OrderId

        var resting = await RestingOrders(order.Symbol, Opposite(order.Side), excludeAccountId: accountId);
        var houseQuantity = await HouseQuantityLeft(tick!, order.Side);
        var book = resting
            .Select(o => new Liquidity(o.OrderId, o.LimitPrice!.Value, o.RemainingQty))
            .Append(new Liquidity(null, tick!.Price, houseQuantity));

        var cashLimit = order.Side == "BUY" && order.OrderType == "MARKET" ? balances.AvailableCash : (decimal?)null;
        var fills = OrderMatcher.Match(order.Side, order.LimitPrice, order.Quantity, book, cashLimit);
        var byId = resting.ToDictionary(o => o.OrderId);
        await ApplyFills(order, fills.Select(f => (f, f.RestingOrderId == null ? null : byId[f.RestingOrderId.Value])), tick!);

        if (order.RemainingQty > 0 && order.OrderType == "MARKET")
        {
            // Market orders never rest: whatever couldn't be filled now is cancelled
            order.Status = OrderStatus.Cancelled;
            order.StatusReason = order.QuantityFilled == 0
                ? "No liquidity available."
                : $"Only {order.QuantityFilled:0.####} of {order.Quantity:0.####} could be filled; the rest was cancelled.";
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return order;
    }

    // Cancels a working order. Order is null if the account has no such order;
    // Cancelled is false if it had already filled or been cancelled.
    public async Task<(Order? Order, bool Cancelled)> CancelOrder(int accountId, long orderId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockAccount(accountId);

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == orderId && o.AccountId == accountId);
        if (order == null) return (null, false);

        await LockSecurity(order.Symbol);
        await _db.Entry(order).ReloadAsync(); // a tick may have filled it while we waited for the lock

        if (!OrderStatus.Working.Contains(order.Status))
            return (order, false);

        order.Status = OrderStatus.Cancelled;
        order.StatusReason = "Cancelled by user.";
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return (order, true);
    }

    // Fills resting limit orders that the latest tick crosses, against the house quote.
    // Called by the price feed after each tick.
    public async Task MatchRestingOrders(string symbol)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockSecurity(symbol);

        var tick = await LatestTick(symbol);
        if (tick == null) return;

        foreach (var side in new[] { "BUY", "SELL" })
        {
            var crossed = (await RestingOrders(symbol, side, excludeAccountId: null))
                .Where(o => side == "BUY" ? o.LimitPrice >= tick.Price : o.LimitPrice <= tick.Price)
                .ToList();
            if (crossed.Count == 0) continue;

            var houseLeft = await HouseQuantityLeft(tick, side);
            foreach (var order in crossed)
            {
                if (houseLeft <= 0) break;
                var qty = Math.Min(order.RemainingQty, houseLeft);
                houseLeft -= qty;
                await ApplyFills(order, new[] { (new Fill(null, tick.Price, qty), (Order?)null) }, tick);
            }
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    // Cash and, if a symbol is given, that position, read in a single statement so a fill
    // committing mid-read can't make the available amounts look larger than they are.
    public async Task<Balances> GetBalances(int accountId, string? symbol = null)
    {
        var working = OrderStatus.Working;
        var result = await _db.Accounts
            .Where(a => a.AccountId == accountId)
            .Select(a => new
            {
                Cash = _db.LedgerEntries
                    .Where(l => l.AccountId == accountId && l.EntryType == "CASH")
                    .Sum(l => (decimal?)l.Amount) ?? 0,
                ReservedCash = _db.Orders
                    .Where(o => o.AccountId == accountId && o.Side == "BUY" && o.OrderType == "LIMIT" && working.Contains(o.Status))
                    .Sum(o => (decimal?)(o.LimitPrice * (o.Quantity - o.QuantityFilled))) ?? 0,
                Position = _db.LedgerEntries
                    .Where(l => l.AccountId == accountId && l.EntryType == "POSITION" && l.Symbol == symbol)
                    .Sum(l => (decimal?)l.Amount) ?? 0,
                ReservedShares = _db.Orders
                    .Where(o => o.AccountId == accountId && o.Side == "SELL" && o.Symbol == symbol && working.Contains(o.Status))
                    .Sum(o => (decimal?)(o.Quantity - o.QuantityFilled)) ?? 0,
            })
            .FirstAsync();

        return new Balances(result.Cash, result.ReservedCash, result.Position, result.ReservedShares);
    }

    private static Order Normalize(PlaceOrderRequest request)
    {
        var symbol = request.Symbol?.Trim().ToUpperInvariant() ?? "";
        var side = request.Side?.Trim().ToUpperInvariant() ?? "";
        var type = request.OrderType?.Trim().ToUpperInvariant() ?? "";

        if (symbol == "") throw new OrderValidationException("Symbol is required.");
        if (side is not ("BUY" or "SELL")) throw new OrderValidationException("Side must be BUY or SELL.");
        if (type is not ("MARKET" or "LIMIT")) throw new OrderValidationException("Order type must be MARKET or LIMIT.");
        if (request.Quantity <= 0 || request.Quantity != Math.Floor(request.Quantity))
            throw new OrderValidationException("Quantity must be a positive whole number of shares.");
        if (request.Quantity > MaxQuantity)
            throw new OrderValidationException($"Quantity is limited to {MaxQuantity:N0} shares per order.");

        decimal? limit = null;
        if (type == "LIMIT")
        {
            if (request.LimitPrice is not > 0)
                throw new OrderValidationException("Limit orders need a positive limit price.");
            limit = Math.Round(request.LimitPrice.Value, 4);
        }

        return new Order { Symbol = symbol, Side = side, OrderType = type, LimitPrice = limit, Quantity = request.Quantity };
    }

    private static string? CheckFunds(Order order, PriceTick? tick, Balances balances)
    {
        if (tick == null)
            return $"{order.Symbol} has no price yet.";

        if (order.Side == "SELL")
            return balances.AvailableShares >= order.Quantity ? null
                : $"Not enough shares: {balances.AvailableShares:0.####} {order.Symbol} available to sell.";

        var cost = (order.LimitPrice ?? tick.Price) * order.Quantity;
        return balances.AvailableCash >= cost ? null
            : $"Not enough cash: {cost:0.00} needed, {balances.AvailableCash:0.00} available.";
    }

    // Writes executions and ledger entries for each fill and updates both orders' fill status
    private async Task ApplyFills(Order order, IEnumerable<(Fill Fill, Order? Resting)> fills, PriceTick tick)
    {
        var pending = new List<(Order Order, Execution Execution)>();
        foreach (var (fill, resting) in fills)
        {
            pending.Add((order, AddExecution(order, resting, fill, tick)));
            if (resting != null)
                pending.Add((resting, AddExecution(resting, order, fill, tick)));
        }
        if (pending.Count == 0) return;

        await _db.SaveChangesAsync(); // assigns ExecutionIds for the ledger references

        foreach (var (o, execution) in pending)
        {
            var (cash, position) = OrderMatcher.LedgerDeltas(o.Side, execution.Price, execution.Quantity);
            _db.LedgerEntries.AddRange(
                new LedgerEntry { AccountId = o.AccountId, EntryType = "CASH", Amount = cash, ReferenceType = "TRADE", ReferenceId = execution.ExecutionId },
                new LedgerEntry { AccountId = o.AccountId, EntryType = "POSITION", Symbol = o.Symbol, Amount = position, ReferenceType = "TRADE", ReferenceId = execution.ExecutionId });

            o.QuantityFilled += execution.Quantity;
            o.Status = o.RemainingQty <= 0 ? OrderStatus.Filled : OrderStatus.Partial;
        }
    }

    private Execution AddExecution(Order order, Order? counter, Fill fill, PriceTick tick)
    {
        var execution = new Execution
        {
            OrderId = order.OrderId,
            CounterOrderId = counter?.OrderId,
            PriceTickId = counter == null ? tick.PriceTickId : null,
            Symbol = order.Symbol,
            Price = fill.Price,
            Quantity = fill.Quantity,
        };
        _db.Executions.Add(execution);
        return execution;
    }

    // Working limit orders on one side of the book, in priority order
    private Task<List<Order>> RestingOrders(string symbol, string side, int? excludeAccountId)
    {
        var working = OrderStatus.Working;
        var query = _db.Orders.Where(o => o.Symbol == symbol && o.Side == side && o.OrderType == "LIMIT" && working.Contains(o.Status));
        if (excludeAccountId != null)
            query = query.Where(o => o.AccountId != excludeAccountId); // no self-trades
        return (side == "BUY" ? query.OrderByDescending(o => o.LimitPrice) : query.OrderBy(o => o.LimitPrice))
            .ThenBy(o => o.OrderId)
            .ToListAsync();
    }

    // How many shares the house will still trade with `side` orders at this tick
    private async Task<decimal> HouseQuantityLeft(PriceTick tick, string side)
    {
        var used = await _db.Executions
            .Where(e => e.PriceTickId == tick.PriceTickId)
            .Join(_db.Orders, e => e.OrderId, o => o.OrderId, (e, o) => new { e.Quantity, o.Side })
            .Where(x => x.Side == side)
            .SumAsync(x => (decimal?)x.Quantity) ?? 0;
        return Math.Max(0, _options.HouseDepth - used);
    }

    private Task<PriceTick?> LatestTick(string symbol) =>
        _db.PriceTicks
            .Where(p => p.Symbol == symbol)
            .OrderByDescending(p => p.PriceTickId)
            .FirstOrDefaultAsync();

    private Task LockAccount(int accountId) =>
        _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Accounts\" WHERE \"AccountId\" = {accountId} FOR UPDATE");

    // Returns false if the symbol doesn't exist
    private async Task<bool> LockSecurity(string symbol) =>
        (await _db.Database
            .SqlQuery<string>($"SELECT \"Symbol\" AS \"Value\" FROM \"Securities\" WHERE \"Symbol\" = {symbol} FOR UPDATE")
            .ToListAsync()).Count == 1;

    private static string Opposite(string side) => side == "BUY" ? "SELL" : "BUY";
}
