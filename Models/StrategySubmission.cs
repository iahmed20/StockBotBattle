// Models/StrategySubmission.cs
// A strategy version submitted for execution; versions are immutable, so later edits don't change it
public class StrategySubmission
{
    public int StrategySubmissionId { get; set; }
    public int AccountId { get; set; }          // the owner
    public int? TradingAccountId { get; set; }  // the account the bot trades for; null means AccountId
    public int? RoundId { get; set; }           // set when the bot trades in a competition round
    public Guid StrategyId { get; set; }
    public int StrategyVersion { get; set; }
    public string Status { get; set; } = SubmissionStatus.Queued;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public int TicksProcessed { get; set; }  // price ticks the strategy has answered
    public DateTime? LastTickAt { get; set; }
    public string? Error { get; set; }  // why the run failed or stopped
    public string? Log { get; set; }    // tail of the bot's printed output

    public StrategyVersion? Version { get; set; }

    public int TradesFor => TradingAccountId ?? AccountId;
}

public static class SubmissionStatus
{
    public const string Queued = "QUEUED";
    public const string Running = "RUNNING";
    public const string Completed = "COMPLETED"; // ran until the time limit or the end of its round
    public const string Stopped = "STOPPED";     // stopped by the user or replaced by a newer submission
    public const string Failed = "FAILED";
}
