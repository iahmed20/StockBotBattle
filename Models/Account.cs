// Models/Account.cs
public class Account
{
    public int AccountId { get; set; }
    public string OwnerName { get; set; } = "";
    public string? Email { get; set; } // stored lowercased; unique
    public int? RoundId { get; set; }  // set on round accounts, which only trade within their round
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
