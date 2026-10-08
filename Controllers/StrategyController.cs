using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class StrategyController : ControllerBase
{
    private const int MaxCodeLength = 100_000;
    private const int MaxNoteLength = 200;
    private const string DefaultName = "strategy.py";

    private readonly BrokerageContext _db;
    private readonly StrategyRunnerService _runner;

    public StrategyController(BrokerageContext db, StrategyRunnerService runner)
    {
        _db = db;
        _runner = runner;
    }

    public class CodeRequest
    {
        public string Code { get; set; } = "";
        public string? Note { get; set; } // optional "what I changed"
    }

    private int AccountId => User.AccountId();
    private string OwnerId => AccountId.ToString();

    // The editor works on one strategy per account: the most recently created one that isn't deleted
    private Task<Strategy?> CurrentStrategy() =>
        _db.Strategies
            .Where(s => s.OwnerId == OwnerId && s.DeletedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync();

    // GET /api/strategy
    [HttpGet]
    public async Task<IActionResult> GetStrategy()
    {
        var strategy = await CurrentStrategy();
        var current = strategy == null ? null
            : await _db.StrategyVersions.FindAsync(strategy.Id, strategy.CurrentVersion);

        var lastSubmission = await _db.StrategySubmissions
            .Where(s => s.AccountId == AccountId)
            .OrderByDescending(s => s.StrategySubmissionId)
            .Select(s => new { s.StrategySubmissionId, s.StrategyVersion, s.Status, s.SubmittedAt, s.StartedAt, s.FinishedAt, s.Error })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            strategyId = strategy?.Id,
            name = strategy?.Name,
            version = current?.Version,
            code = current?.Code,
            updatedAt = current?.CreatedAt,
            lastSubmission
        });
    }

    // GET /api/strategy/versions
    [HttpGet("versions")]
    public async Task<IActionResult> ListVersions()
    {
        var strategy = await CurrentStrategy();
        if (strategy == null) return Ok(Array.Empty<object>());

        var versions = await _db.StrategyVersions
            .Where(v => v.StrategyId == strategy.Id)
            .OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.CreatedAt, v.Note })
            .ToListAsync();

        return Ok(versions);
    }

    // GET /api/strategy/versions/3
    [HttpGet("versions/{version:int}")]
    public async Task<IActionResult> GetVersion(int version)
    {
        var strategy = await CurrentStrategy();
        if (strategy == null) return NotFound();

        var v = await _db.StrategyVersions.FindAsync(strategy.Id, version);
        if (v == null) return NotFound();

        return Ok(new { v.Version, v.Code, v.CreatedAt, v.Note });
    }

    // PUT /api/strategy
    // Saves the code as a new version, unless it is identical to the current version
    [HttpPut]
    public async Task<IActionResult> SaveStrategy([FromBody] CodeRequest request)
    {
        var error = Validate(request);
        if (error != null) return BadRequest(error);

        var (strategy, version, created) = await SaveVersion(request);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return VersionConflict();
        }

        return Ok(new { strategyId = strategy.Id, version = version.Version, updatedAt = version.CreatedAt, created });
    }

    // POST /api/strategy/submit
    // Saves the code, then queues that exact version for the sandbox runner; it replaces
    // any submission of this account that is already running
    [HttpPost("submit")]
    public async Task<IActionResult> SubmitStrategy([FromBody] CodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
            return BadRequest("Cannot submit an empty strategy.");
        var error = Validate(request);
        if (error != null) return BadRequest(error);

        var (strategy, version, _) = await SaveVersion(request);
        var submission = new StrategySubmission
        {
            AccountId = AccountId,
            StrategyId = strategy.Id,
            StrategyVersion = version.Version
        };
        _db.StrategySubmissions.Add(submission);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            return VersionConflict();
        }

        _db.AuditLogs.Add(new AuditLog
        {
            AccountId = AccountId,
            Action = "STRATEGY_SUBMITTED",
            Detail = $"Submitted strategy #{submission.StrategySubmissionId} ({strategy.Name} v{version.Version})"
        });
        await _db.SaveChangesAsync();

        return Ok(new
        {
            strategyId = strategy.Id,
            version = version.Version,
            updatedAt = version.CreatedAt,
            submission.StrategySubmissionId,
            submission.StrategyVersion,
            submission.Status,
            submission.SubmittedAt
        });
    }

    // GET /api/strategy/submissions
    // Submission history, newest first
    [HttpGet("submissions")]
    public async Task<IActionResult> ListSubmissions()
    {
        var submissions = await _db.StrategySubmissions
            .Where(s => s.AccountId == AccountId)
            .OrderByDescending(s => s.StrategySubmissionId)
            .Take(50)
            .Select(s => new { s.StrategySubmissionId, s.StrategyVersion, s.Status, s.SubmittedAt, s.StartedAt, s.FinishedAt, s.Error })
            .ToListAsync();
        return Ok(submissions);
    }

    // GET /api/strategy/submissions/7
    // One submission with the bot's output and the orders it placed
    [HttpGet("submissions/{id:int}")]
    public async Task<IActionResult> GetSubmission(int id)
    {
        var submission = await _db.StrategySubmissions
            .Where(s => s.StrategySubmissionId == id && s.AccountId == AccountId)
            .Select(s => new { s.StrategySubmissionId, s.StrategyVersion, s.Status, s.SubmittedAt, s.StartedAt, s.FinishedAt, s.Error, s.Log })
            .FirstOrDefaultAsync();
        if (submission == null) return NotFound();

        var orders = await _db.Orders
            .Where(o => o.StrategySubmissionId == id)
            .OrderByDescending(o => o.OrderId)
            .Take(200)
            .ToListAsync();

        return Ok(new { submission, orders });
    }

    // POST /api/strategy/submissions/7/stop
    [HttpPost("submissions/{id:int}/stop")]
    public async Task<IActionResult> StopSubmission(int id)
    {
        if (!await _runner.StopAsync(id, AccountId, "Stopped by user."))
            return NotFound("No queued or running submission with that id.");

        _db.AuditLogs.Add(new AuditLog { AccountId = AccountId, Action = "STRATEGY_STOP_REQUESTED", Detail = $"Stopped submission #{id}" });
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private static string? Validate(CodeRequest request)
    {
        if (request.Code.Length > MaxCodeLength)
            return $"Strategy code is limited to {MaxCodeLength:N0} characters.";
        if (request.Note?.Length > MaxNoteLength)
            return $"Version notes are limited to {MaxNoteLength} characters.";
        return null;
    }

    // Stages a new version (and the strategy itself on first save); the caller saves changes
    private async Task<(Strategy, StrategyVersion, bool created)> SaveVersion(CodeRequest request)
    {
        var strategy = await CurrentStrategy();
        if (strategy == null)
        {
            strategy = new Strategy { OwnerId = OwnerId, Name = DefaultName };
            _db.Strategies.Add(strategy);
        }
        else
        {
            var current = await _db.StrategyVersions.FindAsync(strategy.Id, strategy.CurrentVersion);
            if (current != null && current.Code == request.Code)
                return (strategy, current, false);
        }

        var version = new StrategyVersion
        {
            StrategyId = strategy.Id,
            Version = strategy.CurrentVersion + 1,
            Code = request.Code,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim()
        };
        _db.StrategyVersions.Add(version);
        strategy.CurrentVersion = version.Version;
        return (strategy, version, true);
    }

    // Two saves raced for the same version number (StrategyVersions primary key),
    // or two first saves raced to create the strategy (unique OwnerId + Name)
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private ObjectResult VersionConflict() =>
        Conflict("This strategy was saved from somewhere else at the same time. Reload to get the latest version.");
}
