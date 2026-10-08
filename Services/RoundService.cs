// Services/RoundService.cs
using Microsoft.EntityFrameworkCore;

public class RoundOptions
{
    public bool Enabled { get; set; } = true;
    public int DurationMinutes { get; set; } = 30;
    public int BreakMinutes { get; set; } = 2;           // lobby time between rounds
    public decimal StartingCash { get; set; } = 100_000;
    public int CheckIntervalSeconds { get; set; } = 5;
}

public record LeaderboardRow(
    int Rank, int AccountId, string Player, decimal Equity, decimal ReturnPct, int Trades,
    string? BotStatus, int? BotVersion, DateTime JoinedAt);

// Runs competition rounds back to back: there is always one round open to join, it goes ACTIVE
// at its start time, and when it ends its open orders are cancelled, its bots are stopped, and
// final equity and ranks are saved.
public class RoundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RoundOptions _options;
    private readonly StrategyRunnerService _runner;
    private readonly ILogger<RoundService> _logger;

    public RoundService(IServiceScopeFactory scopes, RoundOptions options, StrategyRunnerService runner, ILogger<RoundService> logger)
    {
        _scopes = scopes;
        _options = options;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await AdvanceAsync(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Advancing competition rounds failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.CheckIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    // Moves rounds along their lifecycle as of `now`, and makes sure the next round exists
    public async Task AdvanceAsync(DateTime now)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();

        var due = await db.Rounds
            .Where(r => r.Status == RoundStatus.Active && r.EndsAt <= now)
            .Select(r => r.RoundId)
            .ToListAsync();
        foreach (var roundId in due)
            await FinishAsync(roundId);

        await db.Rounds
            .Where(r => r.Status == RoundStatus.Upcoming && r.StartsAt <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RoundStatus.Active));

        if (!await db.Rounds.AnyAsync(r => r.Status == RoundStatus.Upcoming))
        {
            var activeEnd = await db.Rounds
                .Where(r => r.Status == RoundStatus.Active)
                .MaxAsync(r => (DateTime?)r.EndsAt);
            var startsAt = (activeEnd ?? now).AddMinutes(_options.BreakMinutes);
            db.Rounds.Add(new Round
            {
                StartsAt = startsAt,
                EndsAt = startsAt.AddMinutes(_options.DurationMinutes),
                StartingCash = _options.StartingCash,
            });
            await db.SaveChangesAsync();
        }
    }

    public async Task FinishAsync(int roundId)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        var reason = $"Round #{roundId} ended.";

        // Mark the round finished first: from here on, new orders for its accounts are rejected
        var finished = await db.Rounds
            .Where(r => r.RoundId == roundId && r.Status == RoundStatus.Active)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RoundStatus.Finished));
        if (finished == 0) return;

        var accounts = await db.RoundEntries.Where(e => e.RoundId == roundId).Select(e => e.TradingAccountId).ToListAsync();

        await _runner.EndRunsAsync(accounts, reason);
        await db.StrategySubmissions
            .Where(s => s.Status == SubmissionStatus.Queued && s.TradingAccountId != null && accounts.Contains(s.TradingAccountId.Value))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, SubmissionStatus.Stopped)
                .SetProperty(s => s.Error, reason)
                .SetProperty(s => s.FinishedAt, DateTime.UtcNow));

        // Cancel resting orders under the account locks, so an order placed just before the
        // round flipped to FINISHED is either fully in (and cancelled here) or rejected
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Accounts\" WHERE \"RoundId\" = {roundId} ORDER BY \"AccountId\" FOR UPDATE");
            var working = OrderStatus.Working;
            await db.Orders
                .Where(o => o.RoundId == roundId && working.Contains(o.Status))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(o => o.Status, OrderStatus.Cancelled)
                    .SetProperty(o => o.StatusReason, reason));
            await transaction.CommitAsync();
        }

        // Final standings at the closing prices
        var rows = await Leaderboard(db, roundId, live: true);
        var entries = await db.RoundEntries.Where(e => e.RoundId == roundId).ToListAsync();
        foreach (var entry in entries)
        {
            var row = rows.Single(r => r.AccountId == entry.AccountId);
            entry.FinalEquity = Math.Round(row.Equity, 2);
            entry.FinalRank = row.Rank;
        }
        await db.SaveChangesAsync();

        _logger.LogInformation("Round {RoundId} finished with {Players} players", roundId, entries.Count);
    }

    // Standings for a round. `live` values positions at the latest prices; otherwise finished
    // rounds report the equity and ranks saved when they ended.
    public static async Task<List<LeaderboardRow>> Leaderboard(BrokerageContext db, int roundId, bool live)
    {
        var round = await db.Rounds.AsNoTracking().FirstAsync(r => r.RoundId == roundId);
        var entries = await db.RoundEntries
            .Where(e => e.RoundId == roundId)
            .Join(db.Accounts, e => e.AccountId, a => a.AccountId, (e, a) => new { Entry = e, Player = a.OwnerName })
            .ToListAsync();
        var tradingIds = entries.Select(e => e.Entry.TradingAccountId).ToList();

        var cash = await db.LedgerEntries
            .Where(l => tradingIds.Contains(l.AccountId) && l.EntryType == "CASH")
            .GroupBy(l => l.AccountId)
            .Select(g => new { AccountId = g.Key, Amount = g.Sum(l => l.Amount) })
            .ToDictionaryAsync(x => x.AccountId, x => x.Amount);
        var positions = await db.LedgerEntries
            .Where(l => tradingIds.Contains(l.AccountId) && l.EntryType == "POSITION")
            .GroupBy(l => new { l.AccountId, l.Symbol })
            .Select(g => new { g.Key.AccountId, g.Key.Symbol, Quantity = g.Sum(l => l.Amount) })
            .ToListAsync();
        var prices = await db.Securities
            .Select(s => new
            {
                s.Symbol,
                Price = db.PriceTicks.Where(p => p.Symbol == s.Symbol).OrderByDescending(p => p.PriceTickId).Select(p => (decimal?)p.Price).FirstOrDefault()
            })
            .ToDictionaryAsync(x => x.Symbol, x => x.Price ?? 0);
        var trades = await db.Orders
            .Where(o => tradingIds.Contains(o.AccountId))
            .Join(db.Executions, o => o.OrderId, e => e.OrderId, (o, e) => o.AccountId)
            .GroupBy(accountId => accountId)
            .Select(g => new { AccountId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AccountId, x => x.Count);
        var bots = (await db.StrategySubmissions
                .Where(s => s.TradingAccountId != null && tradingIds.Contains(s.TradingAccountId.Value))
                .Select(s => new { TradingAccountId = s.TradingAccountId!.Value, s.StrategySubmissionId, s.Status, s.StrategyVersion })
                .ToListAsync())
            .GroupBy(s => s.TradingAccountId)
            .ToDictionary(g => g.Key, g => g.MaxBy(s => s.StrategySubmissionId)!); // each player's latest bot

        var useFinal = !live && round.Status == RoundStatus.Finished;
        var rows = entries.Select(x =>
        {
            var id = x.Entry.TradingAccountId;
            var equity = useFinal && x.Entry.FinalEquity != null
                ? x.Entry.FinalEquity.Value
                : cash.GetValueOrDefault(id) + positions.Where(p => p.AccountId == id).Sum(p => p.Quantity * prices.GetValueOrDefault(p.Symbol!));
            var bot = bots.GetValueOrDefault(id);
            return new
            {
                x.Entry,
                Row = new LeaderboardRow(0, x.Entry.AccountId, x.Player, Math.Round(equity, 2),
                    round.StartingCash == 0 ? 0 : Math.Round((equity / round.StartingCash - 1) * 100, 2),
                    trades.GetValueOrDefault(id), bot?.Status, bot?.StrategyVersion, x.Entry.JoinedAt)
            };
        });

        var ordered = useFinal
            ? rows.OrderBy(r => r.Entry.FinalRank ?? int.MaxValue)
            : rows.OrderByDescending(r => r.Row.Equity).ThenBy(r => r.Row.JoinedAt);
        return ordered.Select((r, i) => r.Row with { Rank = useFinal ? r.Entry.FinalRank ?? i + 1 : i + 1 }).ToList();
    }
}
