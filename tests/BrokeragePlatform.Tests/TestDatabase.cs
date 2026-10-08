using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

// Creates a throwaway PostgreSQL database for one test class and drops it afterwards.
// The server comes from BROKERAGE_TEST_CONNECTION, or else the API's ConnectionStrings:Brokerage
// user secret; the role needs permission to create databases.
public class TestDatabase : IAsyncLifetime
{
    public string ConnectionString { get; }
    private readonly string _adminConnectionString;
    private readonly string _name = $"brokerage_test_{Guid.NewGuid():N}";
    private static int _symbolCounter;

    public TestDatabase()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<TestDatabase>()
            .AddEnvironmentVariables()
            .Build();
        var server = config["BROKERAGE_TEST_CONNECTION"] ?? config.GetConnectionString("Brokerage")
            ?? throw new InvalidOperationException(
                "Integration tests need PostgreSQL: set BROKERAGE_TEST_CONNECTION or the ConnectionStrings:Brokerage user secret.");

        _adminConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = "postgres", Pooling = false }.ConnectionString;
        ConnectionString = new NpgsqlConnectionStringBuilder(server) { Database = _name }.ConnectionString;
    }

    public BrokerageContext CreateContext() =>
        new(new DbContextOptionsBuilder<BrokerageContext>().UseNpgsql(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_adminConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    // A fresh security with one price tick, so tests don't share an order book
    public async Task<string> CreateSecurity(decimal price)
    {
        var symbol = $"T{Interlocked.Increment(ref _symbolCounter)}";
        await using var db = CreateContext();
        db.Securities.Add(new Security { Symbol = symbol, Name = symbol });
        db.PriceTicks.Add(new PriceTick { Symbol = symbol, Price = price });
        await db.SaveChangesAsync();
        return symbol;
    }

    public async Task AddTick(string symbol, decimal price)
    {
        await using var db = CreateContext();
        db.PriceTicks.Add(new PriceTick { Symbol = symbol, Price = price });
        await db.SaveChangesAsync();
    }

    // An account holding `cash` and, optionally, shares of one symbol
    public async Task<int> CreateAccount(decimal cash, string? symbol = null, decimal shares = 0)
    {
        await using var db = CreateContext();
        var account = new Account { OwnerName = "Test" };
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        db.LedgerEntries.Add(new LedgerEntry { AccountId = account.AccountId, EntryType = "CASH", Amount = cash, ReferenceType = "DEPOSIT" });
        if (symbol != null)
            db.LedgerEntries.Add(new LedgerEntry { AccountId = account.AccountId, EntryType = "POSITION", Symbol = symbol, Amount = shares, ReferenceType = "DEPOSIT" });
        await db.SaveChangesAsync();
        return account.AccountId;
    }
}
