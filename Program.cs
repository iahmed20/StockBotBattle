using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Local dev: set with `dotnet user-secrets set ConnectionStrings:Brokerage "..."`; containers: ConnectionStrings__Brokerage
var connectionString = builder.Configuration.GetConnectionString("Brokerage")
    ?? throw new InvalidOperationException("Connection string 'Brokerage' is not configured.");

builder.Services.AddDbContext<BrokerageContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddControllers();
builder.Services.AddHostedService<PriceTickerService>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddScoped<MatchingEngine>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Market").Get<MarketOptions>() ?? new MarketOptions());

// Magic-link email: real SMTP when Email:Smtp:Host is set, otherwise the link is written to the log
var smtpOptions = builder.Configuration.GetSection("Email:Smtp").Get<SmtpOptions>();
if (!string.IsNullOrWhiteSpace(smtpOptions?.Host))
    builder.Services.AddSingleton<IEmailSender>(new SmtpEmailSender(smtpOptions));
else
    builder.Services.AddSingleton<IEmailSender, LoggingEmailSender>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "brokerage.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;

        // This is an API: return 401/403 instead of redirecting to a login page
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var myAllowSpecificOrigins = "_myAllowSpecificOrigins";

builder.Services.AddCors(options =>
{
    options.AddPolicy(name: myAllowSpecificOrigins,
                      policy =>
                      {
                          policy.WithOrigins("http://localhost:5173")
                                .AllowAnyHeader()
                                .AllowAnyMethod()
                                .AllowCredentials(); // lets the browser send the auth cookie
                      });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BrokerageContext>();

    if (!db.Securities.Any())
    {
        db.Securities.AddRange(
            new Security { Symbol = "NEKO", Name = "Neko Corp", Drift = 0.08, Volatility = 0.20 },
            new Security { Symbol = "PAWS", Name = "Paws & Co", Drift = 0.05, Volatility = 0.15 },
            new Security { Symbol = "MEOW", Name = "Meow Industries", Drift = 0.12, Volatility = 0.35 },
            new Security { Symbol = "TUNA", Name = "Tuna Holdings", Drift = 0.03, Volatility = 0.10 },
            new Security { Symbol = "YARN", Name = "Yarn Dynamics", Drift = -0.02, Volatility = 0.50 }
        );
        db.SaveChanges();
    }

    if (!db.Accounts.Any())
    {
        var alice = new Account { OwnerName = "Alice Trader" };
        var bob = new Account { OwnerName = "Bob Investor" };

        db.Accounts.AddRange(alice, bob);
        db.SaveChanges(); 

        db.LedgerEntries.AddRange(
            new LedgerEntry {
                AccountId = alice.AccountId, EntryType = "CASH",
                Amount = 10000, ReferenceType = "DEPOSIT", ReferenceId = 0
            },
            new LedgerEntry {
                AccountId = bob.AccountId, EntryType = "CASH",
                Amount = 5000, ReferenceType = "DEPOSIT", ReferenceId = 0
            }
        );
        db.SaveChanges();
    }
}

app.UseCors(myAllowSpecificOrigins);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();

public partial class Program { } // lets integration tests reference the app
