// Models/Round.cs
// A timed competition. Every player who joins trades a separate account that starts with
// the same cash, and the round's leaderboard ranks those accounts by equity.
public class Round
{
    public int RoundId { get; set; }
    public string Status { get; set; } = RoundStatus.Upcoming;
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public decimal StartingCash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<RoundEntry> Entries { get; set; } = new();
}

public static class RoundStatus
{
    public const string Upcoming = "UPCOMING"; // open to join; trading starts at StartsAt
    public const string Active = "ACTIVE";
    public const string Finished = "FINISHED";
}

// One player in one round. TradingAccountId is the round account they trade with;
// AccountId is their own (signed-in) account.
public class RoundEntry
{
    public int RoundEntryId { get; set; }
    public int RoundId { get; set; }
    public int AccountId { get; set; }
    public int TradingAccountId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public decimal? FinalEquity { get; set; } // set when the round finishes
    public int? FinalRank { get; set; }

    public Round? Round { get; set; }
}
