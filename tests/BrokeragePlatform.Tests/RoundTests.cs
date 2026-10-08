using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

public class RoundTests : IClassFixture<TestDatabase>, IAsyncDisposable
{
    private readonly TestDatabase _test;
    private readonly MarketOptions _market = new() { HouseDepth = 10 };
    private readonly ServiceProvider _services;
    private readonly RoundService _rounds;

    public RoundTests(TestDatabase test)
    {
        _test = test;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BrokerageContext>(o => o.UseNpgsql(test.ConnectionString));
        services.AddScoped<MatchingEngine>();
        services.AddSingleton(_market);
        _services = services.BuildServiceProvider();
        var scopes = _services.GetRequiredService<IServiceScopeFactory>();
        var runner = new StrategyRunnerService(scopes, new SandboxOptions(), new LocalPythonLauncher(), new TickNotifier(),
            NullLogger<StrategyRunnerService>.Instance);
        _rounds = new RoundService(scopes, new RoundOptions(), runner, NullLogger<RoundService>.Instance);
    }

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private async Task<int> CreateRound(string status, decimal startingCash = 1000m)
    {
        await using var db = _test.CreateContext();
        var round = new Round
        {
            Status = status,
            StartsAt = DateTime.UtcNow.AddMinutes(status == RoundStatus.Upcoming ? 5 : -5),
            EndsAt = DateTime.UtcNow.AddMinutes(30),
            StartingCash = startingCash,
        };
        db.Rounds.Add(round);
        await db.SaveChangesAsync();
        return round.RoundId;
    }

    // Mirrors POST /api/rounds/{id}/join; returns the round account
    private async Task<int> Join(int roundId, int playerAccountId)
    {
        await using var db = _test.CreateContext();
        var round = await db.Rounds.SingleAsync(r => r.RoundId == roundId);
        var account = new Account { OwnerName = $"player{playerAccountId}", RoundId = roundId };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        db.RoundEntries.Add(new RoundEntry { RoundId = roundId, AccountId = playerAccountId, TradingAccountId = account.AccountId });
        db.LedgerEntries.Add(new LedgerEntry { AccountId = account.AccountId, EntryType = "CASH", Amount = round.StartingCash, ReferenceType = "ROUND_STAKE", ReferenceId = roundId });
        await db.SaveChangesAsync();
        return account.AccountId;
    }

    private async Task<Order> Place(int accountId, string symbol, string side, decimal qty, decimal? limit = null)
    {
        await using var db = _test.CreateContext();
        return await new MatchingEngine(db, _market).PlaceOrder(accountId,
            new PlaceOrderRequest(symbol, side, limit == null ? "MARKET" : "LIMIT", limit, qty));
    }

    private async Task SetStatus(int roundId, string status)
    {
        await using var db = _test.CreateContext();
        await db.Rounds.Where(r => r.RoundId == roundId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status));
    }

    [Fact]
    public async Task Round_accounts_can_only_trade_while_the_round_is_active()
    {
        var symbol = await _test.CreateSecurity(100m);
        var roundId = await CreateRound(RoundStatus.Upcoming);
        var trader = await Join(roundId, await _test.CreateAccount(0m));

        var early = await Place(trader, symbol, "BUY", 1);
        await SetStatus(roundId, RoundStatus.Active);
        var during = await Place(trader, symbol, "BUY", 1);
        await SetStatus(roundId, RoundStatus.Finished);
        var late = await Place(trader, symbol, "BUY", 1);

        Assert.Equal(OrderStatus.Rejected, early.Status);
        Assert.Contains("hasn't started", early.StatusReason);
        Assert.Equal(OrderStatus.Filled, during.Status);
        Assert.Equal(roundId, during.RoundId);
        Assert.Equal(OrderStatus.Rejected, late.Status);
        Assert.Contains("has ended", late.StatusReason);
    }

    [Fact]
    public async Task Round_order_books_are_separate_from_the_open_market_and_each_other()
    {
        var symbol = await _test.CreateSecurity(100m);
        var outsider = await _test.CreateAccount(0m, symbol, 50);
        var outsiderAsk = await Place(outsider, symbol, "SELL", 50, limit: 100.5m); // open market

        var roundA = await CreateRound(RoundStatus.Active, 100_000m);
        var roundB = await CreateRound(RoundStatus.Active, 100_000m);
        var traderB = await Join(roundB, await _test.CreateAccount(0m));
        await Place(traderB, symbol, "BUY", 10); // uses up round B's house depth for this tick
        var askB = await Place(traderB, symbol, "SELL", 10, limit: 100.6m);
        var buyerA = await Join(roundA, await _test.CreateAccount(0m));

        // Round A gets its own 10 shares from the house and never sees the other books' asks
        var buy = await Place(buyerA, symbol, "BUY", 30, limit: 101m);

        Assert.Equal((OrderStatus.Partial, 10m), (buy.Status, buy.QuantityFilled));
        await using var db = _test.CreateContext();
        Assert.Equal(OrderStatus.Open, (await db.Orders.SingleAsync(o => o.OrderId == outsiderAsk.OrderId)).Status);
        Assert.Equal(OrderStatus.Open, (await db.Orders.SingleAsync(o => o.OrderId == askB.OrderId)).Status);
        Assert.False(await db.Executions.AnyAsync(e => e.OrderId == outsiderAsk.OrderId || e.OrderId == askB.OrderId));
    }

    [Fact]
    public async Task Finishing_a_round_cancels_orders_and_saves_the_final_standings()
    {
        var symbol = await _test.CreateSecurity(100m);
        var roundId = await CreateRound(RoundStatus.Active, 1000m);
        var winner = await _test.CreateAccount(0m);
        var loser = await _test.CreateAccount(0m);
        var idle = await _test.CreateAccount(0m);
        var winnerTrades = await Join(roundId, winner);
        var loserTrades = await Join(roundId, loser);
        await Join(roundId, idle);

        await Place(winnerTrades, symbol, "BUY", 5);               // 500 cash into 5 shares at 100
        await Place(loserTrades, symbol, "BUY", 5);
        await Place(loserTrades, symbol, "SELL", 5, limit: 100m);  // straight back to 1000 cash
        var resting = await Place(loserTrades, symbol, "BUY", 2, limit: 50m);
        await _test.AddTick(symbol, 120m);                          // winner's shares are now worth 600

        await _rounds.FinishAsync(roundId);

        await using var db = _test.CreateContext();
        Assert.Equal(RoundStatus.Finished, (await db.Rounds.SingleAsync(r => r.RoundId == roundId)).Status);
        var cancelled = await db.Orders.SingleAsync(o => o.OrderId == resting.OrderId);
        Assert.Equal((OrderStatus.Cancelled, $"Round #{roundId} ended."), (cancelled.Status, cancelled.StatusReason));

        var board = await RoundService.Leaderboard(db, roundId, live: false);
        // The loser and idle player both end on 1000; the loser joined first, so ranks higher
        Assert.Equal(new[] { winner, loser, idle }, board.Select(r => r.AccountId));
        Assert.Equal(new[] { 1, 2, 3 }, board.Select(r => r.Rank));
        Assert.Equal(new[] { 1100m, 1000m, 1000m }, board.Select(r => r.Equity));
        Assert.Equal(10m, board[0].ReturnPct);
        var entries = await db.RoundEntries.Where(e => e.RoundId == roundId).OrderBy(e => e.FinalRank).ToListAsync();
        Assert.Equal(new[] { winner, loser, idle }, entries.Select(e => e.AccountId));

        // Final standings don't move with later prices
        await _test.AddTick(symbol, 10m);
        var after = await RoundService.Leaderboard(db, roundId, live: false);
        Assert.Equal(1100m, after[0].Equity);
    }

    [Fact]
    public async Task Leaderboard_ranks_live_equity_with_ties_broken_by_join_time()
    {
        var symbol = await _test.CreateSecurity(50m);
        var roundId = await CreateRound(RoundStatus.Active, 1000m);
        var first = await _test.CreateAccount(0m);
        var second = await _test.CreateAccount(0m);
        var trader = await _test.CreateAccount(0m);
        await Join(roundId, first);
        await Join(roundId, second);
        var traderAccount = await Join(roundId, trader);
        await Place(traderAccount, symbol, "BUY", 10);
        await _test.AddTick(symbol, 60m);

        await using var db = _test.CreateContext();
        var board = await RoundService.Leaderboard(db, roundId, live: false);

        Assert.Equal(new[] { trader, first, second }, board.Select(r => r.AccountId));
        Assert.Equal(1100m, board[0].Equity);
        Assert.Equal(1, board[0].Trades); // one fill against the house
        Assert.Equal(0, board[1].Trades);
    }
}

// Lifecycle scheduling gets its own database, since AdvanceAsync looks at every round
public class RoundLifecycleTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _test;
    public RoundLifecycleTests(TestDatabase test) => _test = test;

    [Fact]
    public async Task Rounds_open_start_and_finish_back_to_back()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BrokerageContext>(o => o.UseNpgsql(_test.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var scopes = provider.GetRequiredService<IServiceScopeFactory>();
        var runner = new StrategyRunnerService(scopes, new SandboxOptions(), new LocalPythonLauncher(), new TickNotifier(),
            NullLogger<StrategyRunnerService>.Instance);
        var options = new RoundOptions { DurationMinutes = 30, BreakMinutes = 2, StartingCash = 5000 };
        var rounds = new RoundService(scopes, options, runner, NullLogger<RoundService>.Instance);
        var t0 = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        async Task<List<Round>> All()
        {
            await using var db = _test.CreateContext();
            return await db.Rounds.OrderBy(r => r.RoundId).AsNoTracking().ToListAsync();
        }

        await rounds.AdvanceAsync(t0);
        var first = Assert.Single(await All());
        Assert.Equal((RoundStatus.Upcoming, t0.AddMinutes(2), t0.AddMinutes(32), 5000m),
            (first.Status, first.StartsAt, first.EndsAt, first.StartingCash));

        // At the start time it goes active, and the next round opens right after it
        await rounds.AdvanceAsync(t0.AddMinutes(2));
        var (a, b) = (await All()) switch { var l => (l[0], l[1]) };
        Assert.Equal(RoundStatus.Active, a.Status);
        Assert.Equal((RoundStatus.Upcoming, a.EndsAt.AddMinutes(2)), (b.Status, b.StartsAt));

        // Nothing else changes mid-round
        await rounds.AdvanceAsync(t0.AddMinutes(20));
        Assert.Equal(2, (await All()).Count);

        // At the end it finishes; the next round starts after the break
        await rounds.AdvanceAsync(t0.AddMinutes(32));
        Assert.Equal(new[] { RoundStatus.Finished, RoundStatus.Upcoming }, (await All()).Select(r => r.Status));
        await rounds.AdvanceAsync(t0.AddMinutes(34));
        Assert.Equal(new[] { RoundStatus.Finished, RoundStatus.Active, RoundStatus.Upcoming }, (await All()).Select(r => r.Status));
    }
}
