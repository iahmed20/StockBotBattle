using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Accounts are created on first sign-in (see AuthController); these endpoints only
// serve the signed-in account.
[ApiController]
[Authorize]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private const decimal MaxDeposit = 1_000_000;

    private readonly BrokerageContext _db;
    private readonly MatchingEngine _matchingEngine;

    public AccountsController(BrokerageContext db, MatchingEngine matchingEngine)
    {
        _db = db;
        _matchingEngine = matchingEngine;
    }

    // GET /api/accounts/5
    // Cash and positions, including what is reserved by working orders
    [HttpGet("{id}")]
    public async Task<IActionResult> GetAccount(int id)
    {
        if (id != User.AccountId()) return Forbid();

        var account = await _db.Accounts.FindAsync(id);
        if (account == null) return NotFound();

        var balances = await _matchingEngine.GetBalances(id);

        var working = OrderStatus.Working;
        var held = await _db.LedgerEntries
            .Where(l => l.AccountId == id && l.EntryType == "POSITION")
            .GroupBy(l => l.Symbol!)
            .Select(g => new { Symbol = g.Key, Quantity = g.Sum(l => l.Amount) })
            .ToListAsync();
        var reserved = await _db.Orders
            .Where(o => o.AccountId == id && o.Side == "SELL" && working.Contains(o.Status))
            .GroupBy(o => o.Symbol)
            .Select(g => new { Symbol = g.Key, Quantity = g.Sum(o => o.Quantity - o.QuantityFilled) })
            .ToDictionaryAsync(x => x.Symbol, x => x.Quantity);

        var positions = held
            .Where(p => p.Quantity != 0)
            .OrderBy(p => p.Symbol)
            .Select(p => new { p.Symbol, p.Quantity, Available = p.Quantity - reserved.GetValueOrDefault(p.Symbol) });

        return Ok(new
        {
            account.AccountId,
            account.OwnerName,
            CashBalance = balances.Cash,
            AvailableCash = balances.AvailableCash,
            Positions = positions
        });
    }

    public class DepositRequest
    {
        public decimal Amount { get; set; }
    }

    // POST /api/accounts/5/deposit
    [HttpPost("{id}/deposit")]
    public async Task<IActionResult> Deposit(int id, [FromBody] DepositRequest request)
    {
        if (id != User.AccountId()) return Forbid();
        if (request.Amount <= 0)
            return BadRequest("Deposit amount must be positive, nyaa.");
        if (request.Amount > MaxDeposit)
            return BadRequest($"Deposits are limited to {MaxDeposit:N0} at a time.");

        var account = await _db.Accounts.FindAsync(id);
        if (account == null) return NotFound();

        var entry = new LedgerEntry
        {
            AccountId = id,
            EntryType = "CASH",
            Symbol = null,
            Amount = Math.Round(request.Amount, 2),
            ReferenceType = "DEPOSIT",
            ReferenceId = 0
        };

        _db.LedgerEntries.Add(entry);

        _db.AuditLogs.Add(new AuditLog
        {
            AccountId = id,
            Action = "DEPOSIT",
            Detail = $"Deposited {entry.Amount:C} into account {id}"
        });

        await _db.SaveChangesAsync();

        var newBalance = await _db.LedgerEntries
            .Where(l => l.AccountId == id && l.EntryType == "CASH")
            .SumAsync(l => (decimal?)l.Amount) ?? 0;

        return Ok(new { AccountId = id, DepositedAmount = entry.Amount, NewBalance = newBalance });
    }
}
