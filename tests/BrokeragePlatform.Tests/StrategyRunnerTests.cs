using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// Runs sandbox/runner.py with the local python3 instead of Docker. Test-only: no isolation.
public class LocalPythonLauncher : ISandboxLauncher
{
    private static readonly string SandboxDir = FindSandboxDir();

    public Process Start(int submissionId) => Process.Start(new ProcessStartInfo("python3", "runner.py")
    {
        WorkingDirectory = SandboxDir,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    })!;

    public Task RemoveAsync(int submissionId) => Task.CompletedTask;
    public Task RemoveAllAsync() => Task.CompletedTask;

    private static string FindSandboxDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "sandbox", "runner.py")))
                return Path.Combine(dir.FullName, "sandbox");
        throw new InvalidOperationException("Couldn't find sandbox/runner.py");
    }
}

public class StrategyRunnerTests : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private readonly TestDatabase _test;
    private readonly ServiceProvider _services;
    private readonly StrategyRunnerService _runner;
    private readonly TickNotifier _ticks = new();

    public StrategyRunnerTests(TestDatabase test)
    {
        _test = test;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<BrokerageContext>(o => o.UseNpgsql(test.ConnectionString));
        services.AddScoped<MatchingEngine>();
        services.AddSingleton(new MarketOptions());
        _services = services.BuildServiceProvider();

        var options = new SandboxOptions { PollIntervalSeconds = 1, TickTimeoutSeconds = 1, StartupTimeoutSeconds = 10 };
        _runner = new StrategyRunnerService(_services.GetRequiredService<IServiceScopeFactory>(), options,
            new LocalPythonLauncher(), _ticks, NullLogger<StrategyRunnerService>.Instance);
    }

    public Task InitializeAsync() => _runner.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _runner.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
    }

    private async Task<int> Submit(int accountId, string code)
    {
        await using var db = _test.CreateContext();
        var strategy = new Strategy { OwnerId = accountId.ToString(), Name = $"s{Guid.NewGuid():N}", CurrentVersion = 1 };
        db.Strategies.Add(strategy);
        db.StrategyVersions.Add(new StrategyVersion { StrategyId = strategy.Id, Version = 1, Code = code });
        var submission = new StrategySubmission { AccountId = accountId, StrategyId = strategy.Id, StrategyVersion = 1 };
        db.StrategySubmissions.Add(submission);
        await db.SaveChangesAsync();
        return submission.StrategySubmissionId;
    }

    private async Task<StrategySubmission> Get(int submissionId)
    {
        await using var db = _test.CreateContext();
        return await db.StrategySubmissions.AsNoTracking().SingleAsync(s => s.StrategySubmissionId == submissionId);
    }

    // Keeps publishing ticks until the condition holds
    private async Task Until(Func<Task<bool>> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            _ticks.Publish();
            await Task.Delay(200);
        }
    }

    [Fact]
    public async Task Running_strategy_places_orders_and_sees_their_results()
    {
        var symbol = await _test.CreateSecurity(10m);
        var account = await _test.CreateAccount(100m);
        var id = await Submit(account, $$"""
            from datamodel import Order

            def on_tick(symbol, price, state):
                if symbol != "{{symbol}}":
                    return None
                for r in state.last_results:
                    print("result", r.status, r.reason)
                print("cash", state.available_cash, "history", len(state.history[symbol]))
                return [Order.buy(symbol, 4), Order("{{symbol}}", "HOLD", 1)]
            """);

        // Three buys of 4 at 10 = 120 > 100, so the third is rejected for cash
        await Until(async () => (await Get(id)).Log?.Contains("Not enough cash") == true);

        await using (var db = _test.CreateContext())
        {
            var orders = await db.Orders.Where(o => o.StrategySubmissionId == id).OrderBy(o => o.OrderId).ToListAsync();
            Assert.Equal(new[] { OrderStatus.Filled, OrderStatus.Filled, OrderStatus.Rejected }, orders.Take(3).Select(o => o.Status));
            Assert.All(orders, o => Assert.Equal(account, o.AccountId));
        }

        Assert.True(await _runner.StopAsync(id, account, "Stopped by user."));
        var submission = await Get(id);
        Assert.Equal(SubmissionStatus.Stopped, submission.Status);
        Assert.Contains("result FILLED", submission.Log);
        Assert.Contains("Side must be BUY or SELL", submission.Log); // the malformed HOLD order
        Assert.Contains("cash 60", submission.Log);
        Assert.True(submission.TicksProcessed >= 3);
        Assert.NotNull(submission.LastTickAt);
    }

    [Fact]
    public async Task Two_argument_on_tick_still_works_and_exceptions_are_logged()
    {
        var account = await _test.CreateAccount(0m);
        await _test.CreateSecurity(10m);
        var id = await Submit(account, """
            def on_tick(symbol, price):
                raise ValueError("oops " + symbol)
            """);

        await Until(async () => (await Get(id)).Log?.Contains("ValueError: oops") == true);

        var submission = await Get(id);
        Assert.Equal(SubmissionStatus.Running, submission.Status);
        Assert.Contains("strategy.py\", line 2", submission.Log);
        await _runner.StopAsync(id, account, "done");
    }

    [Fact]
    public async Task Strategy_that_fails_to_load_is_marked_failed()
    {
        var account = await _test.CreateAccount(0m);
        var id = await Submit(account, "def on_tick(:\n");

        await Until(async () => (await Get(id)).Status == SubmissionStatus.Failed);

        Assert.Contains("SyntaxError", (await Get(id)).Error);
    }

    [Fact]
    public async Task Strategy_that_exceeds_the_tick_time_limit_is_stopped()
    {
        var account = await _test.CreateAccount(0m);
        await _test.CreateSecurity(10m);
        var id = await Submit(account, """
            def on_tick(symbol, price, state):
                while True:
                    pass
            """);

        await Until(async () => (await Get(id)).Status == SubmissionStatus.Failed);

        Assert.Contains("longer than 1s", (await Get(id)).Error);
    }

    [Fact]
    public async Task New_submission_replaces_the_running_one()
    {
        var account = await _test.CreateAccount(0m);
        const string code = "def on_tick(symbol, price):\n    print('tick')\n";
        var first = await Submit(account, code);
        await Until(async () => (await Get(first)).Log?.Contains("tick") == true);

        var second = await Submit(account, code);
        await Until(async () => (await Get(second)).Status == SubmissionStatus.Running);

        var replaced = await Get(first);
        Assert.Equal(SubmissionStatus.Stopped, replaced.Status);
        Assert.Equal($"Replaced by submission #{second}.", replaced.Error);
        await _runner.StopAsync(second, account, "done");
    }

    [Fact]
    public async Task Submissions_can_only_be_stopped_by_their_owner()
    {
        var account = await _test.CreateAccount(0m);
        var id = await Submit(account, "def on_tick(symbol, price):\n    pass\n");

        Assert.False(await _runner.StopAsync(id, account + 1000, "nope"));
        Assert.True(await _runner.StopAsync(id, account, "Stopped by user.")); // queued or already running

        var submission = await Get(id);
        Assert.Equal(SubmissionStatus.Stopped, submission.Status);
        Assert.Equal("Stopped by user.", submission.Error);
    }

    [Fact]
    public async Task Round_bot_waits_for_its_round_and_completes_when_it_ends()
    {
        var symbol = await _test.CreateSecurity(10m);
        var player = await _test.CreateAccount(0m);
        int roundId, tradingAccount, id;
        await using (var db = _test.CreateContext())
        {
            var round = new Round { Status = RoundStatus.Upcoming, StartsAt = DateTime.UtcNow, EndsAt = DateTime.UtcNow.AddMinutes(30), StartingCash = 1000 };
            db.Rounds.Add(round);
            await db.SaveChangesAsync();
            var account = new Account { OwnerName = "p", RoundId = round.RoundId };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.RoundEntries.Add(new RoundEntry { RoundId = round.RoundId, AccountId = player, TradingAccountId = account.AccountId });
            db.LedgerEntries.Add(new LedgerEntry { AccountId = account.AccountId, EntryType = "CASH", Amount = 1000, ReferenceType = "ROUND_STAKE" });

            var strategy = new Strategy { OwnerId = player.ToString(), Name = "r", CurrentVersion = 1 };
            db.Strategies.Add(strategy);
            db.StrategyVersions.Add(new StrategyVersion { StrategyId = strategy.Id, Version = 1, Code = $$"""
                from datamodel import Order
                def on_tick(symbol, price, state):
                    if symbol == "{{symbol}}":
                        return Order.buy(symbol, 1)
                """ });
            var submission = new StrategySubmission
            {
                AccountId = player, TradingAccountId = account.AccountId, RoundId = round.RoundId,
                StrategyId = strategy.Id, StrategyVersion = 1,
            };
            db.StrategySubmissions.Add(submission);
            await db.SaveChangesAsync();
            (roundId, tradingAccount, id) = (round.RoundId, account.AccountId, submission.StrategySubmissionId);
        }

        // Upcoming: the runner leaves it queued
        await Task.Delay(2500);
        Assert.Equal(SubmissionStatus.Queued, (await Get(id)).Status);

        await using (var db = _test.CreateContext())
            await db.Rounds.Where(r => r.RoundId == roundId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RoundStatus.Active));
        await Until(async () =>
        {
            await using var db = _test.CreateContext();
            return await db.Orders.AnyAsync(o => o.StrategySubmissionId == id && o.Status == OrderStatus.Filled);
        });

        await using (var db = _test.CreateContext())
        {
            var order = await db.Orders.FirstAsync(o => o.StrategySubmissionId == id);
            Assert.Equal((tradingAccount, (int?)roundId), (order.AccountId, order.RoundId)); // traded with the round account
            await db.Rounds.Where(r => r.RoundId == roundId).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RoundStatus.Finished));
        }
        await Until(async () => (await Get(id)).Status == SubmissionStatus.Completed);
        Assert.Equal($"Round #{roundId} ended.", (await Get(id)).Error);
    }
}
