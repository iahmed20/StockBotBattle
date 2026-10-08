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

    // GET /api/orders?status=OPEN
    // The signed-in account's orders, newest first
    [HttpGet]
    public async Task<IActionResult> ListOrders([FromQuery] string? status = null, [FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var query = _db.Orders.Where(o => o.AccountId == User.AccountId());
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
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.OrderId == id && o.AccountId == User.AccountId());
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
    }

    // POST /api/orders
    // Places an order for the signed-in account. 422 with the saved order if it was rejected.
    [HttpPost]
    public async Task<IActionResult> SubmitOrder([FromBody] SubmitOrderRequest request)
    {
        Order order;
        try
        {
            order = await _matchingEngine.PlaceOrder(User.AccountId(),
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
        var (order, cancelled) = await _matchingEngine.CancelOrder(User.AccountId(), id);
        if (order == null) return NotFound();
        if (!cancelled)
            return Conflict($"Order {id} is {order.Status} and can no longer be cancelled.");
        return Ok(order);
    }
}
