public class OrderMatcherTests
{
    [Fact]
    public void Buy_takes_lowest_prices_first_across_book_and_house()
    {
        var book = new[]
        {
            new Liquidity(1, 102m, 5),
            new Liquidity(null, 100m, 3), // house
            new Liquidity(2, 101m, 5),
        };

        var fills = OrderMatcher.Match("BUY", null, 10, book);

        Assert.Equal(new[]
        {
            new Fill(null, 100m, 3),
            new Fill(2, 101m, 5),
            new Fill(1, 102m, 2),
        }, fills);
    }

    [Fact]
    public void Sell_takes_highest_bids_first()
    {
        var book = new[] { new Liquidity(1, 98m, 5), new Liquidity(2, 99m, 5), new Liquidity(null, 97m, 100) };

        var fills = OrderMatcher.Match("SELL", null, 7, book);

        Assert.Equal(new[] { new Fill(2, 99m, 5), new Fill(1, 98m, 2) }, fills);
    }

    [Fact]
    public void Equal_prices_fill_resting_orders_oldest_first_then_the_house()
    {
        var book = new[] { new Liquidity(null, 100m, 10), new Liquidity(7, 100m, 2), new Liquidity(9, 100m, 2) };

        var fills = OrderMatcher.Match("BUY", null, 5, book);

        Assert.Equal(new[] { new Fill(7, 100m, 2), new Fill(9, 100m, 2), new Fill(null, 100m, 1) }, fills);
    }

    [Fact]
    public void Limit_order_ignores_liquidity_past_its_limit()
    {
        var book = new[] { new Liquidity(1, 100m, 5), new Liquidity(2, 100.5m, 5), new Liquidity(3, 101m, 5) };

        var fills = OrderMatcher.Match("BUY", 100.5m, 20, book);

        Assert.Equal(new[] { new Fill(1, 100m, 5), new Fill(2, 100.5m, 5) }, fills);
        Assert.Empty(OrderMatcher.Match("SELL", 101m, 20, book.Take(2)));
    }

    [Fact]
    public void Partial_fill_when_liquidity_runs_out()
    {
        var fills = OrderMatcher.Match("BUY", null, 50, new[] { new Liquidity(null, 100m, 30) });

        Assert.Equal(30, fills.Sum(f => f.Quantity));
    }

    [Fact]
    public void Cash_limit_stops_a_buy_at_whole_shares()
    {
        var book = new[] { new Liquidity(null, 100m, 3), new Liquidity(1, 110m, 10) };

        var fills = OrderMatcher.Match("BUY", null, 10, book, cashLimit: 550m);

        // 3 @ 100 = 300, leaving 250 -> 2 @ 110
        Assert.Equal(new[] { new Fill(null, 100m, 3), new Fill(1, 110m, 2) }, fills);
    }

    [Fact]
    public void Empty_liquidity_is_skipped()
    {
        var fills = OrderMatcher.Match("BUY", null, 5, new[] { new Liquidity(null, 100m, 0), new Liquidity(1, 101m, 5) });

        Assert.Equal(new[] { new Fill(1, 101m, 5) }, fills);
    }

    [Theory]
    [InlineData("BUY", -250, 10)]
    [InlineData("SELL", 250, -10)]
    public void Ledger_deltas_move_cash_and_shares_in_opposite_directions(string side, decimal cash, decimal position)
    {
        Assert.Equal((cash, position), OrderMatcher.LedgerDeltas(side, 25m, 10));
    }
}
