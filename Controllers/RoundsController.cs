using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Competition rounds: join, see standings, and see your round account
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class RoundsController : ControllerBase
{
    private readonly BrokerageContext _db;
    private readonly MatchingEngine _matchingEngine;

    public RoundsController(BrokerageContext db, MatchingEngine matchingEngine)
    {
        _db = db;
        _matchingEngine = matchingEngine;
    }

    private object Summary(Round r, int players, bool joined) => new
    {
        r.RoundId,
        r.Status,
        r.StartsAt,
        r.EndsAt,
        r.StartingCash,
        Players = players,
        Joined = joined,
    };

    private async Task<List<object>> Summaries(IQueryable<Round> rounds)
    {
        var me = User.AccountId();
        var rows = await rounds
            .Select(r => new
            {
                Round = r,
                Players = r.Entries.Count,
                Joined = r.Entries.Any(e => e.AccountId == me),
            })
            .ToListAsync();
        return rows.Select(x => Summary(x.Round, x.Players, x.Joined)).ToList();
    }

    // GET /api/rounds/current
    // The active round (if any), the next round, and the most recent finished round
    [HttpGet("current")]
    public async Task<IActionResult> Current()
    {
        var active = await Summaries(_db.Rounds.Where(r => r.Status == RoundStatus.Active).OrderBy(r => r.StartsAt).Take(1));
        var upcoming = await Summaries(_db.Rounds.Where(r => r.Status == RoundStatus.Upcoming).OrderBy(r => r.StartsAt).Take(1));
        var last = await Summaries(_db.Rounds.Where(r => r.Status == RoundStatus.Finished).OrderByDescending(r => r.EndsAt).Take(1));

        return Ok(new
        {
            Active = active.FirstOrDefault(),
            Upcoming = upcoming.FirstOrDefault(),
            LastFinished = last.FirstOrDefault(),
            ServerTime = DateTime.UtcNow, // lets clients correct countdowns for clock drift
        });
    }

    // GET /api/rounds?limit=20
    // Recent rounds, newest first
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        return Ok(await Summaries(_db.Rounds.OrderByDescending(r => r.StartsAt).Take(limit)));
    }

    // GET /api/rounds/5
    // The round, its leaderboard, and your round account if you joined
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var summary = (await Summaries(_db.Rounds.Where(r => r.RoundId == id))).FirstOrDefault();
        if (summary == null) return NotFound();

        var me = User.AccountId();
        var leaderboard = await RoundService.Leaderboard(_db, id, live: false);
        var entry = await _db.RoundEntries.FirstOrDefaultAsync(e => e.RoundId == id && e.AccountId == me);
        var you = entry == null ? null : await Portfolios.Load(_db, _matchingEngine, entry.TradingAccountId);

        return Ok(new
        {
            Round = summary,
            Leaderboard = leaderboard.Select(r => new
            {
                r.Rank,
                r.Player,
                IsYou = r.AccountId == me,
                r.Equity,
                r.ReturnPct,
                r.Trades,
                r.BotStatus,
                r.BotVersion,
                r.JoinedAt,
            }),
            You = you,
        });
    }

    // POST /api/rounds/5/join
    // Creates your account for the round, funded with the round's starting cash
    [HttpPost("{id:int}/join")]
    public async Task<IActionResult> Join(int id)
    {
        var me = User.AccountId();
        var round = await _db.Rounds.FindAsync(id);
        if (round == null) return NotFound();
        if (round.Status == RoundStatus.Finished) return Conflict($"Round #{id} has already ended.");

        var player = await _db.Accounts.FindAsync(me);
        if (player == null) return Unauthorized();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var tradingAccount = new Account { OwnerName = player.OwnerName, RoundId = id };
        _db.Accounts.Add(tradingAccount);
        await _db.SaveChangesAsync();

        var entry = new RoundEntry { RoundId = id, AccountId = me, TradingAccountId = tradingAccount.AccountId };
        _db.RoundEntries.Add(entry);
        _db.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = tradingAccount.AccountId,
            EntryType = "CASH",
            Amount = round.StartingCash,
            ReferenceType = "ROUND_STAKE",
            ReferenceId = id,
        });
        _db.AuditLogs.Add(new AuditLog { AccountId = me, Action = "ROUND_JOINED", Detail = $"Joined round #{id}" });

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Conflict($"You've already joined round #{id}.");
        }
        await transaction.CommitAsync();

        return Ok(new { round.RoundId, entry.TradingAccountId, Cash = round.StartingCash });
    }
}
