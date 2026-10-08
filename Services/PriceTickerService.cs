// Services/PriceTickerService.cs
using Microsoft.EntityFrameworkCore;

public class PriceTickerService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<PriceTickerService> _logger;
    private readonly Random _random = new();

    // Each tick simulates one trading day, so annualized drift/volatility apply with dt = 1/252
    private const double Dt = 1.0 / 252.0;

    public PriceTickerService(IServiceProvider services, ILogger<PriceTickerService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = _services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
                var securities = await db.Securities.ToListAsync();

                foreach (var security in securities)
                {
                    var lastTick = await db.PriceTicks
                        .Where(p => p.Symbol == security.Symbol)
                        .OrderByDescending(p => p.Timestamp)
                        .FirstOrDefaultAsync();

                    double lastPrice = (double)(lastTick?.Price ?? 100m);

                    // Geometric Brownian motion (exact solution over one step):
                    // S(t+dt) = S(t) * exp((mu - sigma^2 / 2) * dt + sigma * sqrt(dt) * Z)
                    double mu = security.Drift;
                    double sigma = security.Volatility;
                    double z = NextStandardNormal();
                    double newPrice = lastPrice * Math.Exp(
                        (mu - 0.5 * sigma * sigma) * Dt + sigma * Math.Sqrt(Dt) * z);

                    db.PriceTicks.Add(new PriceTick { Symbol = security.Symbol, Price = (decimal)newPrice });
                }

                await db.SaveChangesAsync();

                // Resting limit orders that the new prices cross fill against the house
                foreach (var security in securities)
                {
                    using var matchScope = _services.CreateScope();
                    try
                    {
                        await matchScope.ServiceProvider.GetRequiredService<MatchingEngine>().MatchRestingOrders(security.Symbol);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Matching resting orders for {Symbol} failed", security.Symbol);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    // Box-Muller transform: two uniform samples -> one standard normal sample
    private double NextStandardNormal()
    {
        double u1 = 1.0 - _random.NextDouble(); // (0, 1], avoids Log(0)
        double u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
} 