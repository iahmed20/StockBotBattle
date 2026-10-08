# Stock Bot Battle

A multiplayer stock market for writing Python trading bots. Five fictional, cat-themed stocks move on a live price feed, and players compete in timed rounds: everyone starts with the same cash, bots trade against each other and a house market maker, and a live leaderboard ranks the results. Bots run in locked-down Docker sandboxes, and every trade lands in an append-only ledger. Inspired by trading competitions like IMC Prosperity.


<img width="1029" height="763" alt="Screenshot 2026-10-08 at 3 52 22 PM" src="https://github.com/user-attachments/assets/94fb2e35-13a5-4e2a-8346-e38b60b40895" />

<img width="1093" height="871" alt="Screenshot 2026-10-08 at 4 07 36 PM" src="https://github.com/user-attachments/assets/a510a8c7-5c5e-4f1e-9eed-56e4a2a78e0c" />


**Frontend repo:** [iahmed20/vite-frontend](https://github.com/iahmed20/vite-frontend)

## Contents

- [What you can do](#what-you-can-do)
- [Getting started](#getting-started)
- [Writing a strategy](#writing-a-strategy)
- [How it works](#how-it-works)
- [API reference](#api-reference)
- [Configuration](#configuration)
- [Development](#development)
- [Project status](#project-status)

## What you can do

1. **Sign in with your email.** No password. You get a single-use link that expires after 15 minutes.
2. **Add cash.** Accounts start empty. Use the **Deposit** button in the toolbar, which also shows your available cash and positions.
3. **Watch the market.** NEKO, PAWS, MEOW, TUNA, and YARN tick every 10 seconds. The chart groups ticks into 1m, 5m, 15m, or 1h candles.
4. **Write a bot.** The editor next to the chart saves every change as a numbered version.
5. **Join a round.** Rounds run back to back: 30 minutes each, with a 2-minute break between them. Click **Join** in the Competition panel under the editor to get a separate round account with $100,000.
6. **Submit your bot to the round.** Pick the round under **Run in** next to Submit. If the round hasn't started, the bot waits and starts automatically. The run panel under the editor shows its status, a tick counter, the orders it placed, and everything it prints. Submitting again replaces the running bot, and **Stop** ends it.
7. **Climb the leaderboard.** Players are ranked live by equity: cash plus shares at the latest price. When the round ends, open orders are cancelled, bots stop, and the final standings are saved.

You can also run a bot in the **open market**, outside any round, with your own account and deposits. Open-market results don't count toward any leaderboard.

## Getting started

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download), Docker, and Node.js 22 or later.

### 1. Start PostgreSQL

Use any PostgreSQL 17 server, or run one in Docker:

```bash
docker run --name brokerage-db -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17
```

### 2. Configure and create the database

From the repo root:

```bash
dotnet user-secrets set ConnectionStrings:Brokerage "Host=localhost;Port=5432;Database=brokerage;Username=postgres;Password=postgres"
dotnet tool install --global dotnet-ef
dotnet ef database update
```

The API doesn't apply migrations on startup, so run `dotnet ef database update` again after pulling new migrations. In a container, set the `ConnectionStrings__Brokerage` environment variable instead of the user secret.

### 3. Build the sandbox image

```bash
docker build -t stock-bot-sandbox sandbox/
```

Rebuild it whenever anything in `sandbox/` changes. The API only uses the local image and never pulls one from a registry.

### 4. Run the API

```bash
dotnet run --launch-profile http
```

The API listens on http://localhost:5078, with Swagger UI at http://localhost:5078/swagger in Development. Use the `http` profile, because the frontend expects that address. On first launch, the API seeds the five stocks and the price feed starts ticking.

### 5. Run the frontend

```bash
git clone https://github.com/iahmed20/vite-frontend
cd vite-frontend/vite-frontend-BP
npm install
npm run dev
```

Open http://localhost:5173. The API's CORS policy only allows this origin. To point the frontend at a different API, set `VITE_API_URL`.

### 6. Sign in

Enter your email on the sign-in page. Without SMTP configured, the API doesn't send email. Instead it logs the message, including the sign-in link, as a warning in its console. Open that link to sign in.

## Writing a strategy

A strategy is a Python file that defines `on_tick`. On every price tick, the runner calls `on_tick` once for each stock. To trade, return an `Order` or a list of orders. To do nothing, return `None`.

```python
from datamodel import Order, State

def on_tick(symbol: str, price: float, state: State):
    if symbol == "NEKO" and state.positions.get("NEKO", 0) == 0:
        return Order.buy("NEKO", 10)   # buy 10 shares at market
```

> **Replace the template; don't paste inside it.** Your code should start at the left margin. If you paste a strategy inside the editor's starter `def on_tick(...)`, Python either rejects the indentation or runs the outer function, which never calls yours, so the bot runs but never trades.

### Orders

| Call | Meaning |
|---|---|
| `Order.buy(symbol, quantity)` | Market buy: fills now at the best available prices. Anything that can't fill is cancelled |
| `Order.sell(symbol, quantity)` | Market sell |
| `Order.buy(symbol, quantity, limit_price=p)` | Limit buy: pays at most `p`. Any part that doesn't fill right away rests on the book |
| `Order.sell(symbol, quantity, limit_price=p)` | Limit sell: receives at least `p` |

Quantities are whole shares. You can only buy with cash you have and only sell shares you hold; there is no short selling. An order that breaks these rules is rejected, and the reason shows up in the next tick's `state.last_results`. Bots can't cancel resting orders, so a limit at the current price is a good way to get an immediate fill with price protection.

### State

To receive `state`, give `on_tick` a third parameter. A two-parameter `on_tick(symbol, price)` also works.

| Field | Contents |
|---|---|
| `timestamp` | Time of the latest tick |
| `prices` | Latest price per symbol |
| `history` | The last 50 prices per symbol, oldest first |
| `cash`, `available_cash` | Cash, and cash not reserved by your resting buy orders |
| `positions` | Shares held per symbol |
| `open_orders` | Your resting limit orders |
| `last_results` | What happened to the orders you returned on the previous tick, with rejection reasons |

All the `on_tick` calls for one tick see the same `State`, and orders are placed after the last call returns. Module-level variables keep their values between ticks while the bot runs. They reset if the API restarts, because the bot resumes in a fresh sandbox.

### Example: mean reversion

This bot buys a stock when it falls well below its 20-tick average, and sells when it recovers to the average or drops 5% below the purchase price. It prints one line per stock per tick so you can follow its decisions in the run panel.

```python
import numpy as np
from datamodel import Order, State

LOOKBACK = 20          # ticks used for the moving average
ENTRY_Z = -1.0         # buy when price is 1 standard deviation below the average
EXIT_Z = 0.0           # sell once price is back at the average
STOP_LOSS = 0.05       # or once it's 5% below what we paid
MAX_WEIGHT = 0.15      # never put more than 15% of the account into one stock

entry_price = {}       # module-level state lives as long as the bot runs


def equity(state: State) -> float:
    """Cash plus the market value of every position."""
    return state.cash + sum(qty * state.prices.get(sym, 0) for sym, qty in state.positions.items())


def on_tick(symbol: str, price: float, state: State):
    history = state.history.get(symbol, [])
    if len(history) < LOOKBACK:
        return None  # not enough data yet

    window = np.array(history[-LOOKBACK:])
    std = window.std()
    if std == 0:
        return None
    z = (price - window.mean()) / std

    held = state.positions.get(symbol, 0)
    print(f"{symbol} {price:.2f}  z={z:+.2f}  held={held:g}")

    if any(o.symbol == symbol for o in state.open_orders):
        return None  # wait for the last order to settle before trading again

    # Exit: back to the average, or the stop loss was hit
    if held > 0:
        paid = entry_price.get(symbol, price)
        if z >= EXIT_Z or price <= paid * (1 - STOP_LOSS):
            reason = "take profit" if z >= EXIT_Z else "stop loss"
            print(f"SELL {held:g} {symbol} @ {price:.2f} ({reason}, paid {paid:.2f})")
            entry_price.pop(symbol, None)
            return Order.sell(symbol, int(held), limit_price=price)
        return None

    # Entry: unusually cheap relative to recent prices
    if z <= ENTRY_Z:
        budget = min(equity(state) * MAX_WEIGHT, state.available_cash)
        qty = int(budget // price)
        if qty > 0:
            print(f"BUY {qty} {symbol} @ {price:.2f}")
            entry_price[symbol] = price
            return Order.buy(symbol, qty, limit_price=price)

    return None
```

### Running and debugging

| Status | Meaning |
|---|---|
| `QUEUED` | Waiting for a sandbox to start, usually a few seconds |
| `RUNNING` | Handling ticks. The tick counter goes up every 10 seconds |
| `COMPLETED` | Reached the 60-minute limit |
| `STOPPED` | You stopped it, or a newer submission replaced it |
| `FAILED` | It didn't load (for example, a syntax error), took longer than 2 seconds on a tick, ran out of memory, or crashed. The error is shown in the run panel |

Anything your bot prints appears in the run panel, along with tracebacks when `on_tick` raises. An exception doesn't stop the bot; the next tick calls it again. If the bot is running but you see no output, it's working but has nothing to say yet, so add `print` calls.

**Limits:** code up to 100,000 characters, 2 seconds per tick for all five stocks, 60 minutes per run, 256 MB of memory, half a CPU, and up to 20 orders per tick. NumPy, pandas, and the Python 3.12 standard library are available. There is no network access, and the filesystem is read-only.

## How it works

```
React frontend ──HTTP + auth cookie──▶ ASP.NET Core API ──EF Core──▶ PostgreSQL
                                          │
                                          ├─ PriceTickerService     new prices every 10 s
                                          ├─ MatchingEngine         order books, house market maker, ledger writes
                                          ├─ RoundService           schedules, starts, and settles competition rounds
                                          └─ StrategyRunnerService  one Docker sandbox per running bot
```

| Layer | Technology |
|---|---|
| Backend | C#, ASP.NET Core (.NET 10), Entity Framework Core |
| Database | PostgreSQL (Npgsql) |
| Auth | Cookie sessions with email magic links |
| Bot sandbox | Docker, Python 3.12, NumPy, pandas |
| Frontend | React 19, Vite, Monaco Editor, Lightweight Charts |

**Prices.** Each tick stands for one trading day (dt = 1/252) and applies the exact geometric Brownian motion step, `S(t+dt) = S(t) · exp((μ − σ²/2)·dt + σ·√dt·Z)`, with `Z` drawn by the Box–Muller transform. Each stock has its own drift μ and volatility σ. A stock with no price history starts at 100.

**Matching.** An order trades against the best prices on the other side of the book. That side holds other accounts' resting limit orders plus the house, a market maker that buys and sells at the latest tick price, up to `Market:HouseDepth` shares per side per tick. At equal prices, resting orders fill before the house, oldest first. A market order's unfilled remainder is cancelled, and a limit order's remainder rests on the book. After each tick, resting orders that the new price crosses fill against the house at that price, best price first and then oldest first. Accounts never trade with themselves.

**Funds and concurrency.** Orders are checked against available cash and shares. Cash behind resting buys and shares behind resting sells are reserved so they can't be spent twice. Orders that fail these checks are saved as `REJECTED` with a reason. Placing an order locks the account row and then the stock's row, so simultaneous orders can't overspend and matching for each stock happens one order at a time.

**Ledger.** Balances are never stored or edited. Cash and positions are sums over ledger entries. Each fill writes one execution per order involved, plus a cash entry and a position entry for each account, in the same transaction as the order status update.

**Sign-in.** A magic link carries a random 32-byte token. Only its SHA-256 hash is stored, and redeeming it is a single conditional update, so a link works once even under concurrent requests. Requesting a link returns the same response whether or not the account exists. The account is created on first sign-in.

**Strategies.** Each account has one active strategy. Every save creates an immutable, numbered version, unless the code is unchanged. A submission points at an exact version, so later edits never change a running bot. Simultaneous saves are caught by database constraints and return `409 Conflict`.

**Bot runner.** `StrategyRunnerService` claims queued submissions and starts a container for each, at most one per account. The container has no network, a read-only filesystem, no Linux capabilities, and CPU, memory, and process-count limits. The API sends the code, then one JSON message per tick on stdin. `sandbox/runner.py` calls `on_tick`, captures printed output, and replies with orders on stdout. The orders go through the matching engine for the bot's account. When the API shuts down, running bots go back in the queue and resume on the next start.

**Rounds.** `RoundService` keeps one round open to join at all times. It switches the round to `ACTIVE` at its start time and finishes it at its end time. Joining creates a separate round account funded with the round's starting cash. Round accounts can't take deposits, and each round has its own order books, so round players trade only with each other and the house, never with the open market or another round. Orders from round accounts are rejected before the round starts and after it ends. Finishing a round marks it `FINISHED` first, then cancels open orders under the account locks, stops its bots as `COMPLETED`, and saves each player's final equity and rank. The leaderboard ranks by equity, with ties going to whoever joined first. Finished rounds keep their saved final standings, even as prices move afterwards.

**Audit log.** Account creation, sign-ins, deposits, round joins, and bot submissions, stops, and results are recorded.

## API reference

Interactive docs are at `/swagger` in Development. "Cookie" means the endpoint requires the session cookie from signing in and acts only on your own account.

| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/api/auth/request-link` | None | Email a sign-in link. Body: `{ "email", "ownerName"? }` |
| POST | `/api/auth/verify` | None | Exchange a link token for a session cookie. Body: `{ "token" }` |
| GET | `/api/auth/me` | Cookie | The signed-in account |
| POST | `/api/auth/logout` | None | End the session |
| GET | `/api/accounts/{id}` | Cookie | Cash, available cash, and positions. Other accounts return `403` |
| POST | `/api/accounts/{id}/deposit` | Cookie | Deposit up to 1,000,000 at a time. Body: `{ "amount" }` |
| GET | `/api/orders?status=OPEN&roundId=&limit=100` | Cookie | Your orders, newest first, in the open market or in a round |
| GET | `/api/orders/{id}` | Cookie | One order and its fills |
| POST | `/api/orders` | Cookie | Place an order. Body: `{ "symbol", "side", "orderType", "limitPrice"?, "quantity", "roundId"? }`. With `roundId`, it trades for your round account. Returns `400` if malformed, or `422` with the saved order if rejected |
| DELETE | `/api/orders/{id}` | Cookie | Cancel what's left of a working order. Returns `409` if it's already filled or cancelled |
| GET | `/api/securities` | None | The last 30 ticks for each stock |
| GET | `/api/securities/{symbol}/prices?limit=30` | None | Recent ticks for one stock, oldest first. `limit` is 1–5000 |
| GET | `/api/strategy` | Cookie | Current code and the latest submission |
| PUT | `/api/strategy` | Cookie | Save code as a new version. Body: `{ "code", "note"? }` |
| GET | `/api/strategy/versions` | Cookie | Version history |
| GET | `/api/strategy/versions/{version}` | Cookie | One version's code |
| POST | `/api/strategy/submit` | Cookie | Save and run that exact version, replacing any bot running for the same account. Body: `{ "code", "note"?, "roundId"? }`. With `roundId`, the bot trades in that round |
| GET | `/api/strategy/submissions` | Cookie | Recent submissions with status and tick progress |
| GET | `/api/strategy/submissions/{id}` | Cookie | One submission with its log, tick count, last tick time, and orders |
| POST | `/api/strategy/submissions/{id}/stop` | Cookie | Stop a queued or running bot |
| GET | `/api/rounds/current` | Cookie | The live round, the next round, the last finished round, and the server time |
| GET | `/api/rounds?limit=20` | Cookie | Recent rounds, newest first |
| GET | `/api/rounds/{id}` | Cookie | A round, its leaderboard, and your round account's cash and positions if you joined |
| POST | `/api/rounds/{id}/join` | Cookie | Join a round that hasn't ended. Returns `409` if you already joined |

Example, after signing in with `curl -c cookies.txt`:

```bash
curl -X POST http://localhost:5078/api/orders -b cookies.txt \
  -H "Content-Type: application/json" \
  -d '{"symbol": "MEOW", "side": "BUY", "orderType": "LIMIT", "limitPrice": 95.50, "quantity": 10}'
```

## Configuration

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Brokerage` | required | PostgreSQL connection string |
| `Frontend:BaseUrl` | `http://localhost:5173` | Base URL for sign-in links |
| `Email:Smtp:Host` | empty | SMTP server. When empty, emails are logged instead of sent |
| `Email:Smtp:Port`, `Email:Smtp:EnableSsl` | `587`, `true` | SMTP port and TLS |
| `Email:Smtp:Username`, `Email:Smtp:Password`, `Email:Smtp:From` | empty | SMTP credentials and sender |
| `Market:HouseDepth` | `1000` | Shares the house trades per stock, per side, per tick |
| `Rounds:Enabled` | `true` | Schedule competition rounds |
| `Rounds:DurationMinutes`, `Rounds:BreakMinutes` | `30`, `2` | Round length, and the gap before the next round starts |
| `Rounds:StartingCash` | `100000` | Cash each player's round account starts with |
| `Sandbox:Enabled` | `true` | Run bots. When `false`, submissions stay `QUEUED` |
| `Sandbox:Image`, `Sandbox:DockerPath` | `stock-bot-sandbox`, `docker` | Sandbox image and Docker CLI |
| `Sandbox:Cpus`, `Sandbox:Memory`, `Sandbox:PidsLimit` | `0.5`, `256m`, `64` | Container resource limits |
| `Sandbox:MaxConcurrentRuns` | `10` | Bots that can run at once; the rest wait in the queue |
| `Sandbox:StartupTimeoutSeconds` | `20` | Time to start the container and load the code |
| `Sandbox:TickTimeoutSeconds` | `2` | Time `on_tick` gets for all stocks on one tick |
| `Sandbox:MaxRunMinutes` | `60` | How long a bot runs before it's `COMPLETED` |
| `Sandbox:MaxOrdersPerTick` | `20` | Orders a bot can place per tick |
| `Sandbox:HistoryLength` | `50` | Recent prices per stock sent to the bot |

Keep secrets such as SMTP passwords in user secrets or environment variables, not in `appsettings.json`.

## Development

### Tests

```bash
dotnet test tests/BrokeragePlatform.Tests
```

The suite has unit tests for the matcher, plus integration tests for the matching engine, competition rounds, the API's access rules, and the bot runner. Integration tests need PostgreSQL: each test class creates its own throwaway database and drops it afterwards. They read the `ConnectionStrings:Brokerage` user secret, or `BROKERAGE_TEST_CONNECTION` if set, and the role needs permission to create databases. The runner tests start `sandbox/runner.py` with your local `python3` (3.10 or later) instead of Docker.

### Project structure

```
├── Controllers/        API endpoints
├── Data/               EF Core DbContext and model configuration
├── Migrations/         Database migrations
├── Models/             Account, Order, Execution, LedgerEntry, PriceTick, Round, Strategy, StrategySubmission, ...
├── Services/           MatchingEngine, OrderMatcher, PriceTickerService, RoundService, StrategyRunnerService, sandbox launcher, email
├── sandbox/            Sandbox Dockerfile, runner.py, and datamodel.py
├── tests/              Unit and integration tests
├── Program.cs          Service setup, auth, CORS, and seed data
└── appsettings.json    Non-secret configuration
```

## Project status

**Working:** competition rounds with a live leaderboard, the price feed, market and limit orders on real order books, funds checks, the ledger, magic-link sign-in, deposits, versioned strategies, sandboxed bots with a live run panel, and candlestick charts.

**Known limitations**

- The website has no form for placing or cancelling orders by hand yet; use the API.
- The API runs as a single instance. Running bots and the round schedule live in its process, so a second API process would start duplicate runs.
- One person can join a round more than once by signing in with several email addresses.
- A bot's in-memory variables reset when the API restarts.
- Trades settle immediately.

**Future improvements**

- Order entry, order history, and positions in the frontend
- An all-time leaderboard across rounds
- A multi-stock dashboard instead of a single chart
- Settlement delay modeling (T+1/T+2)
- Technicals, valuations, dividends, and profitability
