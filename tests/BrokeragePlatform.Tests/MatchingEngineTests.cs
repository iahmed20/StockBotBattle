using Microsoft.EntityFrameworkCore;

public class MatchingEngineTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _test;
    private readonly MarketOptions _market = new() { HouseDepth = 10 };

    public MatchingEngineTests(TestDatabase test) => _test = test;

    private async Task<Order> Place(int accountId, string symbol, string side, decimal qty, decimal? limit = null)
    {
        await using var db = _test.CreateContext();
        var request = new PlaceOrderRequest(symbol, side, limit == null ? "MARKET" : "LIMIT", limit, qty);
        return await new MatchingEngine(db, _market).PlaceOrder(accountId, request);
    }

    private async Task<Balances> BalancesOf(int accountId, string symbol)
    {
        await using var db = _test.CreateContext();
        return await new MatchingEngine(db, _market).GetBalances(accountId, symbol);
    }

    private async Task<Order> Reload(long orderId)
    {
        await using var db = _test.CreateContext();
        return await db.Orders.SingleAsync(o => o.OrderId == orderId);
    }

    private async Task MatchResting(string symbol)
    {
        await using var db = _test.CreateContext();
        await new MatchingEngine(db, _market).MatchRestingOrders(symbol);
    }

    [Fact]
    public async Task Market_buy_fills_against_the_house_and_writes_the_ledger()
    {
        var symbol = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(1000m);

        var order = await Place(account, symbol, "BUY", 4);

        Assert.Equal(OrderStatus.Filled, order.Status);
        var balances = await BalancesOf(account, symbol);
        Assert.Equal(600m, balances.Cash);
        Assert.Equal(4m, balances.Position);

        await using var db = _test.CreateContext();
        var execution = await db.Executions.SingleAsync(e => e.OrderId == order.OrderId);
        Assert.Null(execution.CounterOrderId);
        Assert.NotNull(execution.PriceTickId);
        Assert.Equal(2, await db.LedgerEntries.CountAsync(l => l.ReferenceType == "TRADE" && l.ReferenceId == execution.ExecutionId));
    }

    [Fact]
    public async Task Orders_without_enough_cash_or_shares_are_rejected()
    {
        var symbol = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(250m, symbol, 1);

        var buy = await Place(account, symbol, "BUY", 3);
        var sell = await Place(account, symbol, "SELL", 2);

        Assert.Equal(OrderStatus.Rejected, buy.Status);
        Assert.Contains("Not enough cash", buy.StatusReason);
        Assert.Equal(OrderStatus.Rejected, sell.Status);
        Assert.Contains("Not enough shares", sell.StatusReason);
        Assert.Equal(new Balances(250m, 0, 1, 0), await BalancesOf(account, symbol));
    }

    [Theory]
    [InlineData("NOPE", "BUY", "MARKET", null, 1)]
    [InlineData("$SYM", "HOLD", "MARKET", null, 1)]
    [InlineData("$SYM", "BUY", "STOP", null, 1)]
    [InlineData("$SYM", "BUY", "MARKET", null, 0)]
    [InlineData("$SYM", "BUY", "MARKET", null, 1.5)]
    [InlineData("$SYM", "BUY", "LIMIT", null, 1)]
    [InlineData("$SYM", "BUY", "LIMIT", -5.0, 1)]
    public async Task Malformed_orders_throw_and_save_nothing(string symbol, string side, string type, double? limit, double qty)
    {
        var existing = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(1000m);
        await using var db = _test.CreateContext();
        var request = new PlaceOrderRequest(symbol.Replace("$SYM", existing), side, type, (decimal?)limit, (decimal)qty);

        await Assert.ThrowsAsync<OrderValidationException>(() => new MatchingEngine(db, _market).PlaceOrder(account, request));
        Assert.False(await db.Orders.AnyAsync(o => o.AccountId == account));
    }

    [Fact]
    public async Task Limit_buy_below_the_price_rests_and_reserves_cash()
    {
        var symbol = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(1000m);

        var resting = await Place(account, symbol, "BUY", 8, limit: 95m);
        var tooMuch = await Place(account, symbol, "BUY", 3, limit: 95m); // 760 reserved, 240 left

        Assert.Equal(OrderStatus.Open, resting.Status);
        Assert.Equal(OrderStatus.Rejected, tooMuch.Status);
        var balances = await BalancesOf(account, symbol);
        Assert.Equal(1000m, balances.Cash);
        Assert.Equal(240m, balances.AvailableCash);
    }

    [Fact]
    public async Task Cancelling_releases_the_reservation()
    {
        var symbol = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(1000m, symbol, 5);
        var buy = await Place(account, symbol, "BUY", 5, limit: 90m);
        var sell = await Place(account, symbol, "SELL", 5, limit: 120m);

        await using (var db = _test.CreateContext())
        {
            var engine = new MatchingEngine(db, _market);
            Assert.True((await engine.CancelOrder(account, buy.OrderId)).Cancelled);
            Assert.True((await engine.CancelOrder(account, sell.OrderId)).Cancelled);
            Assert.False((await engine.CancelOrder(account, sell.OrderId)).Cancelled); // already cancelled
            Assert.Null((await engine.CancelOrder(account + 1000, buy.OrderId)).Order); // someone else's order
        }

        Assert.Equal(new Balances(1000m, 0, 5, 0), await BalancesOf(account, symbol));
    }

    [Fact]
    public async Task Resting_limit_fills_against_the_house_when_a_tick_crosses_it()
    {
        var symbol = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(1000m);
        var order = await Place(account, symbol, "BUY", 5, limit: 97m);

        await _test.AddTick(symbol, 98m);
        await MatchResting(symbol);
        Assert.Equal(OrderStatus.Open, (await Reload(order.OrderId)).Status);

        await _test.AddTick(symbol, 96.5m);
        await MatchResting(symbol);

        var filled = await Reload(order.OrderId);
        Assert.Equal(OrderStatus.Filled, filled.Status);
        var balances = await BalancesOf(account, symbol);
        Assert.Equal(1000m - 5 * 96.5m, balances.Cash); // filled at the tick price, not the limit
        Assert.Equal(balances.Cash, balances.AvailableCash);
    }

    [Fact]
    public async Task Tick_fills_respect_price_time_priority_and_house_depth()
    {
        var symbol = await _test.CreateSecurity(100m);
        var first = await _test.CreateAccount(10_000m);
        var second = await _test.CreateAccount(10_000m);
        var best = await _test.CreateAccount(10_000m);

        var a = await Place(first, symbol, "BUY", 6, limit: 95m);
        var b = await Place(second, symbol, "BUY", 6, limit: 95m);
        var c = await Place(best, symbol, "BUY", 6, limit: 96m);

        await _test.AddTick(symbol, 94m); // house sells 10 shares this tick
        await MatchResting(symbol);

        Assert.Equal((OrderStatus.Filled, 6m), Summary(await Reload(c.OrderId)));
        Assert.Equal((OrderStatus.Partial, 4m), Summary(await Reload(a.OrderId)));
        Assert.Equal((OrderStatus.Open, 0m), Summary(await Reload(b.OrderId)));
    }

    [Fact]
    public async Task Market_buy_walks_house_then_book_and_cancels_the_rest()
    {
        var symbol = await _test.CreateSecurity(100m);
        var seller = await _test.CreateAccount(0m, symbol, 50);
        var buyer = await _test.CreateAccount(10_000m);

        var ask = await Place(seller, symbol, "SELL", 30, limit: 101m);
        Assert.Equal(OrderStatus.Open, ask.Status);

        var buy = await Place(buyer, symbol, "BUY", 50);

        // 10 from the house at 100, 30 from the resting ask at 101, 10 left over
        Assert.Equal(OrderStatus.Cancelled, buy.Status);
        Assert.Equal(40m, buy.QuantityFilled);
        Assert.Equal(OrderStatus.Filled, (await Reload(ask.OrderId)).Status);

        var buyerBalances = await BalancesOf(buyer, symbol);
        Assert.Equal(10_000m - 10 * 100m - 30 * 101m, buyerBalances.Cash);
        Assert.Equal(40m, buyerBalances.Position);

        var sellerBalances = await BalancesOf(seller, symbol);
        Assert.Equal(30 * 101m, sellerBalances.Cash);
        Assert.Equal(20m, sellerBalances.Position);
    }

    [Fact]
    public async Task Incoming_order_can_partially_fill_a_resting_order()
    {
        var symbol = await _test.CreateSecurity(100m);
        var seller = await _test.CreateAccount(0m, symbol, 30);
        var buyer = await _test.CreateAccount(10_000m);
        await Place(buyer, symbol, "BUY", 10); // uses up this tick's house depth

        var ask = await Place(seller, symbol, "SELL", 30, limit: 102m);
        var bid = await Place(buyer, symbol, "BUY", 12, limit: 102m);

        Assert.Equal((OrderStatus.Filled, 12m), Summary(bid));
        Assert.Equal((OrderStatus.Partial, 12m), Summary(await Reload(ask.OrderId)));
        Assert.Equal(18m, (await BalancesOf(seller, symbol)).Position);
        Assert.Equal(0m, (await BalancesOf(seller, symbol)).AvailableShares); // the other 18 are still offered
    }

    [Fact]
    public async Task Orders_do_not_trade_against_the_same_account()
    {
        var symbol = await _test.CreateSecurity(100m);
        var account = await _test.CreateAccount(10_000m, symbol, 10);
        await Place(await _test.CreateAccount(10_000m), symbol, "BUY", 10); // drain the house

        await Place(account, symbol, "SELL", 10, limit: 101m);
        var buy = await Place(account, symbol, "BUY", 5, limit: 101m);

        Assert.Equal(OrderStatus.Open, buy.Status);
    }

    [Fact]
    public async Task Concurrent_orders_cannot_spend_the_same_cash()
    {
        // Different symbols, so only the account lock (not the per-symbol lock) keeps these apart
        var symbols = new List<string>();
        for (var i = 0; i < 10; i++) symbols.Add(await _test.CreateSecurity(100m));
        var account = await _test.CreateAccount(1000m);

        // Each order costs 300; only three fit in 1000
        var orders = await Task.WhenAll(symbols.Select(async symbol =>
        {
            await using var db = _test.CreateContext();
            return await new MatchingEngine(db, _market).PlaceOrder(account, new PlaceOrderRequest(symbol, "BUY", "MARKET", null, 3));
        }));

        Assert.Equal(3, orders.Count(o => o.Status == OrderStatus.Filled));
        Assert.Equal(7, orders.Count(o => o.Status == OrderStatus.Rejected));
        Assert.Equal(100m, (await BalancesOf(account, symbols[0])).Cash);
    }

    private static (string, decimal) Summary(Order o) => (o.Status, o.QuantityFilled);
}
