// Services/StrategyRunnerService.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

// Runs queued strategy submissions, one sandbox per submission and at most one per trading
// account (a player's own account, or their account in a competition round).
// Every price tick, each running strategy gets a snapshot of the market and its account and
// answers with orders, which go through the MatchingEngine like orders from the API.
public class StrategyRunnerService : BackgroundService
{
    private const int MaxLineChars = 1_000_000; // longest protocol message accepted from a sandbox
    private const int MaxLogChars = 20_000;     // tail of the bot's output kept on the submission
    private const int MaxStderrChars = 4_000;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private readonly IServiceScopeFactory _scopes;
    private readonly SandboxOptions _options;
    private readonly ISandboxLauncher _launcher;
    private readonly TickNotifier _ticks;
    private readonly ILogger<StrategyRunnerService> _logger;
    private readonly ConcurrentDictionary<int, Run> _runs = new(); // by submission id

    private sealed class Run
    {
        public required int SubmissionId { get; init; }
        public required int AccountId { get; init; }        // the owner
        public required int TradingAccountId { get; init; } // whose cash and orders the bot uses
        public int? RoundId { get; init; }
        public CancellationTokenSource Cancel { get; } = new();
        public string? StopReason { get; set; }
        public string StopStatus { get; set; } = SubmissionStatus.Stopped;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    public StrategyRunnerService(IServiceScopeFactory scopes, SandboxOptions options, ISandboxLauncher launcher,
        TickNotifier ticks, ILogger<StrategyRunnerService> logger)
    {
        _scopes = scopes;
        _options = options;
        _launcher = launcher;
        _ticks = ticks;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Sandbox runner is disabled; submissions will stay queued");
            return;
        }

        await RecoverAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartQueuedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Starting queued strategy submissions failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        // Shutting down: runs put their submissions back in the queue so they resume on restart
        foreach (var run in _runs.Values) run.Cancel.Cancel();
        await Task.WhenAll(_runs.Values.Select(r => r.Task));
    }

    // Stops a submission belonging to the account, whether it is running or still queued.
    // Returns false if the account has no such submission in either state.
    public async Task<bool> StopAsync(int submissionId, int accountId, string reason)
    {
        if (_runs.TryGetValue(submissionId, out var run) && run.AccountId == accountId)
        {
            run.StopReason = reason;
            run.Cancel.Cancel();
            await run.Task;
            return true;
        }

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        return await db.StrategySubmissions
            .Where(s => s.StrategySubmissionId == submissionId && s.AccountId == accountId && s.Status == SubmissionStatus.Queued)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, SubmissionStatus.Stopped)
                .SetProperty(s => s.Error, reason)
                .SetProperty(s => s.FinishedAt, DateTime.UtcNow)) > 0;
    }

    // Ends the bots trading for these accounts as COMPLETED, e.g. when their round finishes
    public async Task EndRunsAsync(IReadOnlyCollection<int> tradingAccountIds, string reason)
    {
        var ending = _runs.Values.Where(r => tradingAccountIds.Contains(r.TradingAccountId)).ToList();
        foreach (var run in ending)
        {
            run.StopReason = reason;
            run.StopStatus = SubmissionStatus.Completed;
            run.Cancel.Cancel();
        }
        await Task.WhenAll(ending.Select(r => r.Task));
    }

    // Submissions marked RUNNING by a previous process (crash or restart) go back in the queue
    private async Task RecoverAsync()
    {
        await _launcher.RemoveAllAsync();

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        await db.StrategySubmissions
            .Where(s => s.Status == SubmissionStatus.Running)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, SubmissionStatus.Queued));
    }

    private async Task StartQueuedAsync()
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();

        var queued = await db.StrategySubmissions
            .Where(s => s.Status == SubmissionStatus.Queued)
            .OrderBy(s => s.StrategySubmissionId)
            .ToListAsync();

        // Bots for round accounts only start once their round is ACTIVE
        var tradingIds = queued.Select(s => s.TradesFor).Distinct().ToList();
        var rounds = await db.Accounts
            .Where(a => tradingIds.Contains(a.AccountId) && a.RoundId != null)
            .Join(db.Rounds, a => a.RoundId, r => r.RoundId, (a, r) => new { a.AccountId, r.RoundId, r.Status })
            .ToDictionaryAsync(x => x.AccountId);

        foreach (var forAccount in queued.GroupBy(s => s.TradesFor))
        {
            // Only the newest submission per trading account runs; it replaces anything older
            var latest = forAccount.Last();
            var reason = $"Replaced by submission #{latest.StrategySubmissionId}.";

            foreach (var older in forAccount.SkipLast(1))
                await StopAsync(older.StrategySubmissionId, older.AccountId, reason);

            var round = rounds.GetValueOrDefault(forAccount.Key);
            if (round?.Status == RoundStatus.Finished)
            {
                await StopAsync(latest.StrategySubmissionId, latest.AccountId, $"Round #{round.RoundId} ended.");
                continue;
            }
            if (round?.Status == RoundStatus.Upcoming)
                continue; // stays queued until the round starts

            foreach (var running in _runs.Values.Where(r => r.TradingAccountId == latest.TradesFor).ToList())
                await StopAsync(running.SubmissionId, running.AccountId, reason);

            if (_runs.Count >= _options.MaxConcurrentRuns)
                continue; // stays queued until a slot frees up

            var claimed = await db.StrategySubmissions
                .Where(s => s.StrategySubmissionId == latest.StrategySubmissionId && s.Status == SubmissionStatus.Queued)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, SubmissionStatus.Running)
                    .SetProperty(s => s.StartedAt, DateTime.UtcNow)
                    .SetProperty(s => s.TicksProcessed, 0)
                    .SetProperty(s => s.LastTickAt, (DateTime?)null)
                    .SetProperty(s => s.FinishedAt, (DateTime?)null)
                    .SetProperty(s => s.Error, (string?)null));
            if (claimed == 0) continue; // stopped by its owner in the meantime

            var code = await db.StrategyVersions
                .Where(v => v.StrategyId == latest.StrategyId && v.Version == latest.StrategyVersion)
                .Select(v => v.Code)
                .SingleAsync();

            var run = new Run
            {
                SubmissionId = latest.StrategySubmissionId,
                AccountId = latest.AccountId,
                TradingAccountId = latest.TradesFor,
                RoundId = round?.RoundId,
            };
            _runs[run.SubmissionId] = run;
            run.Task = Task.Run(() => RunAsync(run, code));
        }
    }

    private async Task RunAsync(Run run, string code)
    {
        var log = new StringBuilder();
        var stderr = new StringBuilder();
        string status;
        string? error = null;
        Process? process = null;
        using var timeLimit = new CancellationTokenSource(TimeSpan.FromMinutes(_options.MaxRunMinutes));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(run.Cancel.Token, timeLimit.Token);

        try
        {
            process = _launcher.Start(run.SubmissionId);
            process.ErrorDataReceived += (_, e) =>
            {
                lock (stderr)
                    if (e.Data != null && stderr.Length < MaxStderrChars) stderr.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();
            var reader = new BoundedLineReader(process.StandardOutput, MaxLineChars);

            await Send(process, new { type = "init", code });
            var ready = await Receive(process, reader, _options.StartupTimeoutSeconds, "load", stderr, stop.Token);
            AppendLog(log, ready.Logs, null);
            if (ready.Type == "error")
                throw new SandboxException(ready.Message ?? "The strategy failed to load.");

            var lastResults = new List<OrderResult>();
            while (true)
            {
                await _ticks.WaitForTickAsync(stop.Token);
                if (run.RoundId != null && await RoundFinished(run.RoundId.Value))
                    throw new RoundEndedException(run.RoundId.Value);

                var state = await BuildState(run.TradingAccountId, lastResults);
                await Send(process, new { type = "tick", state });
                var reply = await Receive(process, reader, _options.TickTimeoutSeconds, "on_tick", stderr, stop.Token);
                if (reply.Type != "orders")
                    throw new SandboxException(reply.Message ?? "The sandbox sent an unexpected message.");

                AppendLog(log, reply.Logs, state.Timestamp);
                lastResults = await PlaceOrders(run, reply.Orders ?? new(), log);
                await SaveProgress(run.SubmissionId, log);
            }
        }
        catch (OperationCanceledException) when (timeLimit.IsCancellationRequested && !run.Cancel.IsCancellationRequested)
        {
            status = SubmissionStatus.Completed;
            error = $"Reached the {_options.MaxRunMinutes}-minute time limit.";
        }
        catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested)
        {
            // No reason means the API is shutting down: requeue so the run resumes on restart
            status = run.StopReason == null ? SubmissionStatus.Queued : run.StopStatus;
            error = run.StopReason;
        }
        catch (RoundEndedException ex)
        {
            status = SubmissionStatus.Completed;
            error = $"Round #{ex.RoundId} ended.";
        }
        catch (SandboxException ex)
        {
            status = SubmissionStatus.Failed;
            error = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strategy submission {SubmissionId} crashed", run.SubmissionId);
            status = SubmissionStatus.Failed;
            error = "The strategy runner hit an internal error.";
        }
        finally
        {
            try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } // already exited
            await _launcher.RemoveAsync(run.SubmissionId);
            process?.Dispose();
            _runs.TryRemove(run.SubmissionId, out _);
        }

        await Finish(run, status, error, log);
    }

    private async Task Finish(Run run, string status, string? error, StringBuilder log)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        var logText = log.ToString();
        var finishedAt = status == SubmissionStatus.Queued ? (DateTime?)null : DateTime.UtcNow;
        if (error?.Length > 2000) error = error[..2000];

        await db.StrategySubmissions
            .Where(s => s.StrategySubmissionId == run.SubmissionId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, status)
                .SetProperty(s => s.Error, error)
                .SetProperty(s => s.FinishedAt, finishedAt)
                .SetProperty(s => s.Log, logText));

        if (status != SubmissionStatus.Queued)
        {
            db.AuditLogs.Add(new AuditLog
            {
                AccountId = run.AccountId,
                Action = "STRATEGY_" + status,
                Detail = $"Submission #{run.SubmissionId} {status.ToLowerInvariant()}" + (error == null ? "" : $": {error}")
            });
            await db.SaveChangesAsync();
        }
    }

    private async Task<TickState> BuildState(int accountId, List<OrderResult> lastResults)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        var engine = scope.ServiceProvider.GetRequiredService<MatchingEngine>();

        var history = await db.Securities
            .OrderBy(s => s.Symbol)
            .Select(s => new
            {
                s.Symbol,
                Ticks = db.PriceTicks
                    .Where(p => p.Symbol == s.Symbol)
                    .OrderByDescending(p => p.PriceTickId)
                    .Take(_options.HistoryLength)
                    .Select(p => new { p.Price, p.Timestamp })
                    .ToList()
            })
            .ToListAsync();
        history = history.Where(h => h.Ticks.Count > 0).ToList();

        var balances = await engine.GetBalances(accountId);
        var positions = await db.LedgerEntries
            .Where(l => l.AccountId == accountId && l.EntryType == "POSITION")
            .GroupBy(l => l.Symbol!)
            .Select(g => new { Symbol = g.Key, Quantity = g.Sum(l => l.Amount) })
            .Where(p => p.Quantity != 0)
            .ToDictionaryAsync(p => p.Symbol, p => p.Quantity);
        var working = OrderStatus.Working;
        var openOrders = await db.Orders
            .Where(o => o.AccountId == accountId && working.Contains(o.Status))
            .OrderBy(o => o.OrderId)
            .Select(o => new OpenOrder(o.OrderId, o.Symbol, o.Side, o.Quantity, o.QuantityFilled, o.LimitPrice ?? 0))
            .ToListAsync();

        return new TickState(
            Timestamp: history.Select(h => h.Ticks[0].Timestamp).DefaultIfEmpty(DateTime.UtcNow).Max().ToString("O"),
            Prices: history.ToDictionary(h => h.Symbol, h => h.Ticks[0].Price),
            History: history.ToDictionary(h => h.Symbol, h => h.Ticks.Select(t => t.Price).Reverse().ToList()),
            Cash: balances.Cash,
            AvailableCash: balances.AvailableCash,
            Positions: positions,
            OpenOrders: openOrders,
            LastResults: lastResults);
    }

    private async Task<List<OrderResult>> PlaceOrders(Run run, List<BotOrder> orders, StringBuilder log)
    {
        var results = new List<OrderResult>();
        if (orders.Count > _options.MaxOrdersPerTick)
        {
            AppendLog(log, $"Only the first {_options.MaxOrdersPerTick} of {orders.Count} orders were placed.\n", null);
            orders = orders.Take(_options.MaxOrdersPerTick).ToList();
        }

        foreach (var o in orders)
        {
            using var scope = _scopes.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<MatchingEngine>();
            try
            {
                var order = await engine.PlaceOrder(run.TradingAccountId,
                    new PlaceOrderRequest(o.Symbol ?? "", o.Side ?? "", o.OrderType ?? "", o.LimitPrice, o.Quantity),
                    run.SubmissionId);
                results.Add(new OrderResult(order.OrderId, order.Symbol, order.Side, order.Status, order.QuantityFilled, order.StatusReason));
            }
            catch (OrderValidationException ex)
            {
                results.Add(new OrderResult(null, o.Symbol ?? "", o.Side ?? "", OrderStatus.Rejected, 0, ex.Message));
            }
        }
        return results;
    }

    // Called after every tick, so the editor can show the bot is alive even when it prints nothing
    private async Task<bool> RoundFinished(int roundId)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        return await db.Rounds.AnyAsync(r => r.RoundId == roundId && r.Status == RoundStatus.Finished);
    }

    private sealed class RoundEndedException(int roundId) : Exception
    {
        public int RoundId { get; } = roundId;
    }

    private async Task SaveProgress(int submissionId, StringBuilder log)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();
        var text = log.ToString();
        var now = DateTime.UtcNow;
        await db.StrategySubmissions
            .Where(s => s.StrategySubmissionId == submissionId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Log, text)
                .SetProperty(s => s.TicksProcessed, s => s.TicksProcessed + 1)
                .SetProperty(s => s.LastTickAt, now));
    }

    private static void AppendLog(StringBuilder log, string? text, string? timestamp)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (timestamp != null) log.Append("[").Append(timestamp).Append("]\n");
        log.Append(text);
        if (!text.EndsWith('\n')) log.Append('\n');
        if (log.Length > MaxLogChars) log.Remove(0, log.Length - MaxLogChars);
    }

    private static async Task Send(Process process, object message)
    {
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message, Json));
            await process.StandardInput.FlushAsync();
        }
        catch (IOException)
        {
            throw new SandboxException("The strategy process exited unexpectedly.");
        }
    }

    private static async Task<SandboxMessage> Receive(Process process, BoundedLineReader reader, int timeoutSeconds,
        string stage, StringBuilder stderr, CancellationToken cancellationToken)
    {
        string? line;
        try
        {
            line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new SandboxException($"The strategy took longer than {timeoutSeconds}s to {stage}.");
        }

        if (line == null)
        {
            // Give the process a moment to exit so the exit code and stderr are available
            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(2000));
            string details;
            lock (stderr) details = stderr.ToString().Trim();
            var code = process.HasExited ? process.ExitCode : (int?)null;
            var why = code == 137 ? "it was killed, most likely for using more than the memory limit"
                : code == null ? "it closed its output" : $"exit code {code}";
            throw new SandboxException($"The strategy process exited unexpectedly ({why})." +
                (details.Length > 0 ? $"\n{details}" : ""));
        }

        try
        {
            return JsonSerializer.Deserialize<SandboxMessage>(line, Json)
                ?? throw new SandboxException("The sandbox sent an empty message.");
        }
        catch (JsonException)
        {
            throw new SandboxException("The sandbox sent a message that isn't valid JSON.");
        }
    }

    // Messages to and from sandbox/runner.py, serialized in snake_case
    private record SandboxMessage(string Type, string? Message, string? Logs, List<BotOrder>? Orders);
    private record BotOrder(string? Symbol, string? Side, decimal Quantity, string? OrderType, decimal? LimitPrice);
    private record OpenOrder(long OrderId, string Symbol, string Side, decimal Quantity, decimal QuantityFilled, decimal LimitPrice);
    private record OrderResult(long? OrderId, string Symbol, string Side, string Status, decimal QuantityFilled, string? Reason);
    private record TickState(string Timestamp, Dictionary<string, decimal> Prices, Dictionary<string, List<decimal>> History,
        decimal Cash, decimal AvailableCash, Dictionary<string, decimal> Positions, List<OpenOrder> OpenOrders, List<OrderResult> LastResults);
}

// Reads newline-terminated lines but gives up on any line longer than maxChars,
// so a misbehaving sandbox can't make the API buffer unbounded output
public class BoundedLineReader
{
    private readonly StreamReader _reader;
    private readonly int _maxChars;
    private readonly char[] _buffer = new char[8192];
    private int _start, _end;

    public BoundedLineReader(StreamReader reader, int maxChars)
    {
        _reader = reader;
        _maxChars = maxChars;
    }

    // Returns null at end of stream
    public async Task<string?> ReadLineAsync()
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _reader.ReadAsync(_buffer, 0, _buffer.Length);
                if (_end == 0) return line.Length > 0 ? line.ToString() : null;
            }

            var newline = Array.IndexOf(_buffer, '\n', _start, _end - _start);
            var take = (newline < 0 ? _end : newline) - _start;
            if (line.Length + take > _maxChars)
                throw new SandboxException($"The sandbox sent a message longer than {_maxChars:N0} characters.");

            line.Append(_buffer, _start, take);
            _start += take;
            if (newline >= 0)
            {
                _start++; // skip the newline
                return line.ToString();
            }
        }
    }
}
