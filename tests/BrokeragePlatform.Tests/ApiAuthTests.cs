using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public class CapturingEmailSender : IEmailSender
{
    public string? LastBody;
    public Task SendAsync(string to, string subject, string htmlBody)
    {
        LastBody = htmlBody;
        return Task.CompletedTask;
    }
}

public class ApiFactory : WebApplicationFactory<Program>
{
    public TestDatabase Db { get; } = new();
    public CapturingEmailSender Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Brokerage", Db.ConnectionString);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>(); // no live price feed or bot runner during tests
            services.AddSingleton<IEmailSender>(Email);
        });
    }

    // Signs in through the magic-link flow; the returned client carries the auth cookie
    public async Task<(HttpClient Client, int AccountId)> SignIn(string email)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        (await client.PostAsJsonAsync("/api/auth/request-link", new { email })).EnsureSuccessStatusCode();
        var token = Regex.Match(Email.LastBody!, @"token=([\w-]+)").Groups[1].Value;

        var response = await client.PostAsJsonAsync("/api/auth/verify", new { token });
        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<Me>();
        return (client, me!.AccountId);
    }

    private record Me(int AccountId);
}

static class ServiceCollectionTestExtensions
{
    public static void RemoveAll<T>(this IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(T)).ToList())
            services.Remove(d);
    }
}

public class ApiAuthTests : IClassFixture<ApiAuthTests.Fixture>
{
    public class Fixture : IAsyncLifetime
    {
        public ApiFactory Factory { get; } = new();
        public Task InitializeAsync() => Factory.Db.InitializeAsync();
        public async Task DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Factory.Db.DisposeAsync();
        }
    }

    private readonly ApiFactory _factory;
    public ApiAuthTests(Fixture fixture) => _factory = fixture.Factory;

    [Theory]
    [InlineData("GET", "/api/orders")]
    [InlineData("POST", "/api/orders")]
    [InlineData("DELETE", "/api/orders/1")]
    [InlineData("GET", "/api/accounts/1")]
    [InlineData("POST", "/api/accounts/1/deposit")]
    [InlineData("GET", "/api/strategy/submissions")]
    [InlineData("POST", "/api/strategy/submissions/1/stop")]
    public async Task Trading_endpoints_require_sign_in(string method, string url)
    {
        var client = _factory.CreateClient();
        var request = new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Accounts_are_only_visible_to_their_owner()
    {
        var (alice, aliceId) = await _factory.SignIn("alice@example.com");
        var (_, bobId) = await _factory.SignIn("bob@example.com");

        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync($"/api/accounts/{aliceId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync($"/api/accounts/{bobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await alice.PostAsJsonAsync($"/api/accounts/{bobId}/deposit", new { amount = 100 })).StatusCode);
    }

    [Fact]
    public async Task Orders_are_placed_for_the_signed_in_account_only()
    {
        var symbol = await _factory.Db.CreateSecurity(50m);
        var (carol, carolId) = await _factory.SignIn("carol@example.com");
        var (dave, _) = await _factory.SignIn("dave@example.com");
        (await carol.PostAsJsonAsync($"/api/accounts/{carolId}/deposit", new { amount = 1000 })).EnsureSuccessStatusCode();

        var placed = await carol.PostAsJsonAsync("/api/orders",
            new { symbol, side = "buy", orderType = "limit", limitPrice = 40, quantity = 5 });
        Assert.Equal(HttpStatusCode.OK, placed.StatusCode);
        var order = await placed.Content.ReadFromJsonAsync<Order>();
        Assert.Equal(carolId, order!.AccountId);

        // Dave can't see or cancel Carol's order, and his own order is rejected for lack of cash
        Assert.Empty((await dave.GetFromJsonAsync<List<Order>>("/api/orders"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await dave.DeleteAsync($"/api/orders/{order.OrderId}")).StatusCode);
        var rejected = await dave.PostAsJsonAsync("/api/orders", new { symbol, side = "BUY", orderType = "MARKET", quantity = 1 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await carol.DeleteAsync($"/api/orders/{order.OrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await carol.DeleteAsync($"/api/orders/{order.OrderId}")).StatusCode);
    }

    [Fact]
    public async Task Malformed_orders_return_400()
    {
        var (client, _) = await _factory.SignIn("erin@example.com");

        var response = await client.PostAsJsonAsync("/api/orders", new { symbol = "NOPE", side = "BUY", orderType = "MARKET", quantity = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Players_join_rounds_once_and_see_themselves_on_the_leaderboard()
    {
        int roundId;
        await using (var db = _factory.Db.CreateContext())
        {
            var round = new Round { Status = RoundStatus.Upcoming, StartsAt = DateTime.UtcNow.AddMinutes(1), EndsAt = DateTime.UtcNow.AddMinutes(31), StartingCash = 100_000 };
            db.Rounds.Add(round);
            await db.SaveChangesAsync();
            roundId = round.RoundId;
        }
        var (gina, _) = await _factory.SignIn("gina@example.com");
        var (hank, _) = await _factory.SignIn("hank@example.com");

        Assert.Equal(HttpStatusCode.BadRequest,
            (await gina.PostAsJsonAsync("/api/strategy/submit", new { code = "def on_tick(s, p): pass", roundId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await gina.PostAsync($"/api/rounds/{roundId}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await gina.PostAsync($"/api/rounds/{roundId}/join", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await gina.PostAsJsonAsync("/api/strategy/submit", new { code = "def on_tick(s, p): pass", roundId })).StatusCode);

        var view = await gina.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/rounds/{roundId}");
        var row = Assert.Single(view.GetProperty("leaderboard").EnumerateArray());
        Assert.True(row.GetProperty("isYou").GetBoolean());
        Assert.Equal(100_000m, row.GetProperty("equity").GetDecimal());
        Assert.Equal("QUEUED", row.GetProperty("botStatus").GetString());
        Assert.Equal(100_000m, view.GetProperty("you").GetProperty("cashBalance").GetDecimal());

        // Hank hasn't joined: he sees the board but has no round account
        var hankView = await hank.GetFromJsonAsync<System.Text.Json.JsonElement>($"/api/rounds/{roundId}");
        Assert.False(Assert.Single(hankView.GetProperty("leaderboard").EnumerateArray()).GetProperty("isYou").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, hankView.GetProperty("you").ValueKind);
        var current = await hank.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/rounds/current");
        Assert.Equal(roundId, current.GetProperty("upcoming").GetProperty("roundId").GetInt32());
    }
}
