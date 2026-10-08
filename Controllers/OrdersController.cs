// Controllers/OrdersController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly BrokerageContext _db;
    private readonly MatchingEngine _matchingEngine;

    public OrdersController(BrokerageContext db, MatchingEngine matchingEngine)
    {
        _db = db;
        _matchingEngine = matchingEngine;
    }

    // Your own account, or your account in a round you joined; null if you haven't joined it
    private async Task<int?> TradingAccount(int? roundId)
    {
        if (roundId == null) return User.AccountId();
        return await _db.RoundEntries
            .Where(e => e.RoundId == roundId && e.AccountId == User.AccountId())
            .Select(e => (int?)e.TradingAccountId)
            .FirstOrDefaultAsync();
    }

    // Every account the signed-in player trades with: their own plus their round accounts
    private IQueryable<int> MyAccounts()
    {
        var me = User.AccountId();
        return _db.RoundEntries.Where(e => e.AccountId == me).Select(e => e.TradingAccountId)
            .Concat(_db.Accounts.Where(a => a.AccountId == me).Select(a => a.AccountId));
    }

    // GET /api/orders?status=OPEN&roundId=4
    // Your orders in the open market, or in a round, newest first
    [HttpGet]
    public async Task<IActionResult> ListOrders([FromQuery] string? status = null, [FromQuery] int? roundId = null, [FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var accountId = await TradingAccount(roundId);
        if (accountId == null) return Ok(Array.Empty<Order>());
        var query = _db.Orders.Where(o => o.AccountId == accountId);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(o => o.Status == status.ToUpper());

        var orders = await query.OrderByDescending(o => o.OrderId).Take(limit).ToListAsync();
        return Ok(orders);
    }

    // GET /api/orders/5
    // One order and its fills
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetOrder(long id)
    {
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == id && MyAccounts().Contains(o.AccountId));
        if (order == null) return NotFound();

        var executions = await _db.Executions
            .Where(e => e.OrderId == id)
            .OrderBy(e => e.ExecutionId)
            .Select(e => new { e.ExecutionId, e.Price, e.Quantity, e.ExecutedAt, Counterparty = e.CounterOrderId == null ? "HOUSE" : "ORDER" })
            .ToListAsync();

        return Ok(new { order, executions });
    }

    public class SubmitOrderRequest
    {
        public string Symbol { get; set; } = "";
        public string Side { get; set; } = "";
        public string OrderType { get; set; } = "";
        public decimal? LimitPrice { get; set; }
        public decimal Quantity { get; set; }
        public int? RoundId { get; set; } // trade with your round account instead of your own
    }

    // POST /api/orders
    // Places an order for the signed-in account. 422 with the saved order if it was rejected.
    [HttpPost]
    public async Task<IActionResult> SubmitOrder([FromBody] SubmitOrderRequest request)
    {
        var accountId = await TradingAccount(request.RoundId);
        if (accountId == null) return BadRequest($"Join round #{request.RoundId} before trading in it.");

        Order order;
        try
        {
            order = await _matchingEngine.PlaceOrder(accountId.Value,
                new PlaceOrderRequest(request.Symbol, request.Side, request.OrderType, request.LimitPrice, request.Quantity));
        }
        catch (OrderValidationException ex)
        {
            return BadRequest(ex.Message);
        }

        return order.Status == OrderStatus.Rejected ? UnprocessableEntity(order) : Ok(order);
    }

    // DELETE /api/orders/5
    // Cancels the unfilled part of a working order
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> CancelOrder(long id)
    {
        var accountId = await _db.Orders
            .Where(o => o.OrderId == id && MyAccounts().Contains(o.AccountId))
            .Select(o => (int?)o.AccountId)
            .FirstOrDefaultAsync();
        if (accountId == null) return NotFound();

        var (order, cancelled) = await _matchingEngine.CancelOrder(accountId.Value, id);
        if (order == null) return NotFound();
        if (!cancelled)
            return Conflict($"Order {id} is {order.Status} and can no longer be cancelled.");
        return Ok(order);
    }
}
