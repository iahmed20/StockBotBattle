// Data/BrokerageContext.cs
using Microsoft.EntityFrameworkCore;
public class BrokerageContext : DbContext
{
    public BrokerageContext(DbContextOptions<BrokerageContext> options)
        : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Security> Securities => Set<Security>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Execution> Executions => Set<Execution>();
    public DbSet<PriceTick> PriceTicks => Set<PriceTick>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();
    public DbSet<Strategy> Strategies => Set<Strategy>();
    public DbSet<StrategyVersion> StrategyVersions => Set<StrategyVersion>();
    public DbSet<StrategySubmission> StrategySubmissions => Set<StrategySubmission>();
    public DbSet<Round> Rounds => Set<Round>();
    public DbSet<RoundEntry> RoundEntries => Set<RoundEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Security>().HasKey(s => s.Symbol);

        modelBuilder.Entity<Account>()
            .HasIndex(a => a.Email).IsUnique();

        modelBuilder.Entity<LoginToken>()
            .HasIndex(t => t.TokenHash).IsUnique();

        modelBuilder.Entity<Strategy>(e =>
        {
            e.Property(s => s.OwnerId).HasMaxLength(64);
            e.Property(s => s.Name).HasMaxLength(100);
            e.HasIndex(s => new { s.OwnerId, s.Name })
                .IsUnique()
                .HasFilter("\"DeletedAt\" IS NULL"); // a deleted strategy's name can be reused
        });

        modelBuilder.Entity<StrategyVersion>(e =>
        {
            e.HasKey(v => new { v.StrategyId, v.Version });
            e.Property(v => v.Note).HasMaxLength(200);
            e.HasOne(v => v.Strategy)
                .WithMany(s => s.Versions)
                .HasForeignKey(v => v.StrategyId)
                .OnDelete(DeleteBehavior.Restrict); // strategies are soft-deleted; versions are kept
        });

        modelBuilder.Entity<StrategySubmission>(e =>
        {
            e.HasIndex(s => s.AccountId);
            e.HasIndex(s => s.Status);
            e.Property(s => s.Status).HasMaxLength(16);
            e.Property(s => s.Error).HasMaxLength(2000);
            e.HasOne(s => s.Version)
                .WithMany()
                .HasForeignKey(s => new { s.StrategyId, s.StrategyVersion })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StrategySubmission>()
            .Ignore(s => s.TradesFor);

        modelBuilder.Entity<Round>(e =>
        {
            e.Property(r => r.Status).HasMaxLength(16);
            e.Property(r => r.StartingCash).HasColumnType("decimal(18,4)");
            e.HasIndex(r => r.Status);
        });

        modelBuilder.Entity<RoundEntry>(e =>
        {
            e.HasIndex(x => new { x.RoundId, x.AccountId }).IsUnique(); // one entry per player per round
            e.HasIndex(x => x.TradingAccountId).IsUnique();
            e.Property(x => x.FinalEquity).HasColumnType("decimal(18,4)");
            e.HasOne(x => x.Round).WithMany(r => r.Entries).HasForeignKey(x => x.RoundId);
        });

        modelBuilder.Entity<LedgerEntry>()
            .Property(l => l.Amount).HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Order>(e =>
        {
            e.Property(o => o.StatusReason).HasMaxLength(500);
            e.HasIndex(o => new { o.Symbol, o.Status }); // the working order books for a symbol
            e.HasIndex(o => o.AccountId);
            e.HasIndex(o => o.StrategySubmissionId);
        });

        modelBuilder.Entity<Execution>(e =>
        {
            e.HasIndex(x => x.OrderId);
            e.HasIndex(x => x.PriceTickId); // house depth used at a tick
        });

        modelBuilder.Entity<PriceTick>()
            .HasIndex(p => new { p.Symbol, p.PriceTickId });

        modelBuilder.Entity<Order>()
            .Property(o => o.Quantity).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<Order>()
            .Property(o => o.QuantityFilled).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<Order>()
            .Property(o => o.LimitPrice).HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Execution>()
            .Property(e => e.Price).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<Execution>()
            .Property(e => e.Quantity).HasColumnType("decimal(18,4)");

        modelBuilder.Entity<PriceTick>()
            .Property(p => p.Price).HasColumnType("decimal(18,4)");
    }
}