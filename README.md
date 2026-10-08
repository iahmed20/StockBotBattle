# Stock Bot Battle

A simulated stock market where you trade a small set of fictional, cat-themed stocks and write Python trading bots. Prices move on a live simulated feed, every trade is recorded in an append-only ledger, and your bot code is saved as immutable, numbered versions. Inspired by trading competitions like IMC Prosperity.

> **Status:** The market, order book, ledger, sign-in, strategy editor, and sandboxed bot runner all work. See [Project status](#project-status).

<img width="823" height="613" alt="Stock Bot Battle chart view" src="https://github.com/user-attachments/assets/6ac960af-3f77-40da-ae7f-556e67d71ab0" />

<img width="727" height="803" alt="Stock Bot Battle strategy editor" src="https://github.com/user-attachments/assets/3e2dbee5-d3a3-4efb-be12-2d41fa8bc6f0" />

**Frontend repo:** [iahmed20/vite-frontend](https://github.com/iahmed20/vite-frontend)

## Features

- **Live simulated market.** Five fictional stocks (NEKO, PAWS, MEOW, TUNA, YARN) tick every 10 seconds using geometric Brownian motion, each with its own drift and volatility.
- **Order book.** Market and limit orders match with price-time priority and partial fills, against other accounts' resting orders and a house market maker that quotes the latest price. Limit orders that don't fill right away rest on the book until a later price crosses them or you cancel them.
- **Funds checks.** Orders are checked against available cash and shares, and resting orders reserve what they need. Row locks on the account and the security make concurrent orders safe. Each fill writes its executions, ledger entries, and order status in one transaction.
- **Append-only ledger.** Balances are never stored or edited in place. Cash and positions are derived by summing ledger entries, and every trade writes a cash entry and a position entry linked to its execution.
- **Passwordless sign-in.** Magic links use a random 32-byte token, store only its SHA-256 hash, expire after 15 minutes, and can be redeemed only once, even under concurrent requests. Requesting a link returns the same response whether or not the account exists.
- **Versioned strategy editor.** Each save creates an immutable, numbered version, and identical code isn't saved twice. Submitting queues an exact version for the runner, so later edits never change what was submitted. Simultaneous saves are caught by database constraints and return `409 Conflict`.
- **Sandboxed bots.** Each submitted strategy runs in its own Docker container with no network, a read-only filesystem, and CPU, memory, and time limits. On every price tick it gets the market and its account, and the orders it returns are placed for its account.
- **Audit log.** Account creation, sign-ins, deposits, and strategy submissions, stops, and results are recorded.
- **Candlestick charts.** The React frontend groups raw ticks into 1m, 5m, 15m, or 1h candles, next to a Monaco code editor in resizable panes.

## Tech stack

| Layer | Technology |
|---|---|
| Backend | C#, ASP.NET Core (.NET 10), Entity Framework Core |
| Database | PostgreSQL (Npgsql) |
| Auth | Cookie sessions with email magic links |
| Bot sandbox | Docker, Python 3.12, NumPy, pandas |
| Frontend | React 19, Vite, Monaco Editor, Lightweight Charts |

## How it works

```
React frontend ──HTTP + auth cookie──▶ ASP.NET Core API ──EF Core──▶ PostgreSQL
                                          │
                                          ├─ PriceTickerService     background job, new prices every 10 s
                                          ├─ MatchingEngine         order book and house market maker, writes the ledger
                                          └─ StrategyRunnerService  queued submissions ──▶ one Docker sandbox each
```

**Price feed.** `PriceTickerService` is a hosted background service. Each tick represents one trading day (dt = 1/252) and applies the exact geometric Brownian motion step, `S(t+dt) = S(t) · exp((μ − σ²/2)·dt + σ·√dt·Z)`, with `Z` drawn using the Box–Muller transform. A security with no price history starts at 100.

**Orders.** `POST /api/orders` places an order for the signed-in account. `MatchingEngine` locks the account and the security, checks available cash or shares, and matches the order against the best prices on the other side. That side holds other accounts' resting limit orders plus the house, which buys and sells at the latest tick price up to `Market:HouseDepth` shares per side per tick. At equal prices, resting orders fill before the house, oldest first. A market order's unfilled remainder is cancelled. A limit order's remainder rests on the book, and after each tick any resting order the new price crosses fills against the house at that price, best price and then oldest first. Accounts never trade with themselves. Cash for resting buys and shares for resting sells are reserved, so they can't be spent twice. Orders that fail these checks are saved as `REJECTED` with a reason. Quantities are whole shares.

**Ledger.** Every fill writes one execution per participating order and a cash entry and a position entry for each account involved. Balances are sums over the ledger.

**Bots.** `StrategyRunnerService` claims queued submissions and starts a container for each, at most one per account. A newer submission replaces the running one. The API sends the strategy code and then one message per tick over stdin, and `sandbox/runner.py` replies with orders over stdout. Runs end when you stop them, when they reach the time limit (`COMPLETED`), or when they crash, time out, or exceed the memory limit (`FAILED`). When the API shuts down, running submissions go back in the queue and resume on the next start.

**Strategies.** The editor works with one active strategy per account. Versions are keyed by strategy and version number, and strategy names are unique per owner.

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- PostgreSQL, installed locally or run with Docker
- Docker, to run submitted strategies
- Node.js 22 or later, for the frontend
- Python 3.10 or later, only for running the test suite

### 1. Start PostgreSQL

```bash
docker run --name brokerage-db -e POSTGRES_PASSWORD=postgres -p 5432:5432 -d postgres:17
```

### 2. Set the connection string

From the repo root:

```bash
dotnet user-secrets set ConnectionStrings:Brokerage "Host=localhost;Port=5432;Database=brokerage;Username=postgres;Password=postgres"
```

In a container, set the `ConnectionStrings__Brokerage` environment variable instead.

### 3. Create the database

```bash
dotnet tool install --global dotnet-ef
dotnet ef database update
```

The app doesn't apply migrations on startup, so run this before the first launch.

### 4. Build the sandbox image

```bash
docker build -t stock-bot-sandbox sandbox/
```

Rebuild it after changing anything in `sandbox/`. The API never pulls this image from a registry. If the image is missing or Docker isn't running, submissions fail with an error that explains why. To leave submissions queued instead, set `Sandbox:Enabled` to `false`.

### 5. Run the API

```bash
dotnet run --launch-profile http
```

- API: http://localhost:5078
- Swagger UI: http://localhost:5078/swagger (Development only)

Use the `http` profile, because the frontend expects the API at `http://localhost:5078`. On first launch, the API seeds the five securities and two demo accounts, and the price feed starts ticking.

### 6. Run the frontend

```bash
git clone https://github.com/iahmed20/vite-frontend
cd vite-frontend/vite-frontend-BP
npm install
npm run dev
```

Open http://localhost:5173. The API's CORS policy only allows this origin. To point the frontend at a different API, set `VITE_API_URL`.

### 7. Sign in

Enter your email on the sign-in page. If SMTP isn't configured, the API writes the sign-in email, including the link, to its console log as a warning. Open that link to sign in. New accounts start with no cash; deposit some with `POST /api/accounts/{id}/deposit`.

## Running tests

```bash
dotnet test tests/BrokeragePlatform.Tests
```

The tests need PostgreSQL. Each test class creates its own throwaway database and drops it afterwards. They use the `ConnectionStrings:Brokerage` user secret, or the `BROKERAGE_TEST_CONNECTION` environment variable if it's set, and the database role needs permission to create databases. The runner tests start `sandbox/runner.py` with your local `python3` instead of Docker.

## Configuration

| Key | Required | Default | Purpose |
|---|---|---|---|
| `ConnectionStrings:Brokerage` | Yes | none | PostgreSQL connection string |
| `Frontend:BaseUrl` | No | `http://localhost:5173` | Base URL used to build sign-in links |
| `Email:Smtp:Host` | No | empty | SMTP server. When empty, emails are logged instead of sent |
| `Email:Smtp:Port` | No | `587` | SMTP port |
| `Email:Smtp:Username`, `Email:Smtp:Password` | No | empty | SMTP credentials |
| `Email:Smtp:EnableSsl` | No | `true` | Use TLS for SMTP |
| `Email:Smtp:From` | No | empty | Sender address |
| `Market:HouseDepth` | No | `1000` | Shares the house buys and sells per symbol, per side, per tick |
| `Sandbox:Enabled` | No | `true` | Run submitted strategies. When `false`, they stay `QUEUED` |
| `Sandbox:Image` | No | `stock-bot-sandbox` | Docker image for strategy containers |
| `Sandbox:DockerPath` | No | `docker` | Docker CLI to call |
| `Sandbox:Cpus`, `Sandbox:Memory`, `Sandbox:PidsLimit` | No | `0.5`, `256m`, `64` | Container resource limits |
| `Sandbox:MaxConcurrentRuns` | No | `10` | Submissions that can run at once; the rest wait in the queue |
| `Sandbox:StartupTimeoutSeconds` | No | `20` | Time to start the container and load the strategy |
| `Sandbox:TickTimeoutSeconds` | No | `2` | Time `on_tick` gets for all symbols on one tick |
| `Sandbox:MaxRunMinutes` | No | `60` | How long one submission runs before it's `COMPLETED` |
| `Sandbox:MaxOrdersPerTick` | No | `20` | Orders a strategy can place per tick |
| `Sandbox:HistoryLength` | No | `50` | Recent prices per symbol sent to the strategy |

Keep secrets such as SMTP passwords in user secrets or environment variables, not in `appsettings.json`.

## API reference

Interactive docs are available at `/swagger` in Development.

| Method | Route | Auth | Description |
|---|---|---|---|
| POST | `/api/auth/request-link` | None | Email a sign-in link. Body: `{ "email", "ownerName"? }` |
| POST | `/api/auth/verify` | None | Exchange a link token for a session cookie. Body: `{ "token" }` |
| GET | `/api/auth/me` | Cookie | The signed-in account |
| POST | `/api/auth/logout` | None | End the session |
| GET | `/api/accounts/{id}` | Cookie | Your cash, available cash, and positions. Other accounts return `403` |
| POST | `/api/accounts/{id}/deposit` | Cookie | Deposit cash into your account, up to 1,000,000 at a time. Body: `{ "amount" }` |
| GET | `/api/orders?status=OPEN&limit=100` | Cookie | Your orders, newest first |
| GET | `/api/orders/{id}` | Cookie | One of your orders and its fills |
| POST | `/api/orders` | Cookie | Place an order. Body: `{ "symbol", "side", "orderType", "limitPrice"?, "quantity" }`. Returns `400` for a malformed order and `422` with the saved order if it's rejected |
| DELETE | `/api/orders/{id}` | Cookie | Cancel the unfilled part of a working order. Returns `409` if it's already filled or cancelled |
| GET | `/api/securities` | None | The last 30 ticks for each symbol |
| GET | `/api/securities/{symbol}/prices?limit=30` | None | Recent ticks for one symbol, oldest first. `limit` is 1–5000 |
| GET | `/api/strategy` | Cookie | Current strategy code and the latest submission |
| PUT | `/api/strategy` | Cookie | Save code as a new version. Body: `{ "code", "note"? }` |
| GET | `/api/strategy/versions` | Cookie | Version history |
| GET | `/api/strategy/versions/{version}` | Cookie | One version's code |
| POST | `/api/strategy/submit` | Cookie | Save and queue that exact version, replacing any running submission. Body: `{ "code", "note"? }` |
| GET | `/api/strategy/submissions` | Cookie | Your recent submissions and their status |
| GET | `/api/strategy/submissions/{id}` | Cookie | One submission with its log and the orders it placed |
| POST | `/api/strategy/submissions/{id}/stop` | Cookie | Stop a queued or running submission |

Accounts are created the first time someone signs in, so there is no endpoint for creating one.

Example limit order, using the cookie saved when signing in with `curl -c cookies.txt`:

```bash
curl -X POST http://localhost:5078/api/orders -b cookies.txt \
  -H "Content-Type: application/json" \
  -d '{"symbol": "MEOW", "side": "BUY", "orderType": "LIMIT", "limitPrice": 95.50, "quantity": 10}'
```

## Writing a strategy

New strategies start from this template in the editor:

```python
# strategy.py
# Write your trading strategy here.

def on_tick(symbol: str, price: float) -> None:
    pass
```

On every price tick, `on_tick` is called once for each symbol. To trade, return an `Order` or a list of them. Add a third parameter to receive a `State` snapshot of the market and your account:

```python
import numpy as np
from datamodel import Order, State

def on_tick(symbol: str, price: float, state: State):
    history = state.history[symbol]          # recent prices, oldest first
    held = state.positions.get(symbol, 0)

    if price < np.mean(history) * 0.98 and state.available_cash > price * 10:
        return Order.buy(symbol, 10)                         # market order
    if held and price > np.mean(history) * 1.02:
        return Order.sell(symbol, held, limit_price=price)   # limit order
```

| `State` field | Contents |
|---|---|
| `timestamp` | Time of the latest tick |
| `prices`, `history` | Latest price, and recent prices oldest first, per symbol |
| `cash`, `available_cash` | Cash, and cash not reserved by resting buy orders |
| `positions` | Shares held per symbol |
| `open_orders` | Your resting limit orders |
| `last_results` | What happened to the orders you returned on the previous tick, including rejection reasons |

All the `on_tick` calls for one tick see the same `State`, and orders are placed after the last call returns. Module-level variables persist between ticks for as long as the submission runs. Anything you `print` shows up in the submission's log, along with tracebacks if `on_tick` raises an exception. An exception doesn't stop the bot.

Limits: strategy code can be up to 100,000 characters, and version notes up to 200. Each tick must finish within 2 seconds, and each run lasts up to 60 minutes with 256 MB of memory and half a CPU. Only NumPy, pandas, and the standard library are available, and there is no network access.

## Project structure

```
├── Controllers/        API endpoints
├── Data/               EF Core DbContext and model configuration
├── Migrations/         Database migrations
├── Models/             Entities: Account, Order, Execution, LedgerEntry, PriceTick, Strategy, ...
├── Services/           MatchingEngine, OrderMatcher, PriceTickerService, StrategyRunnerService, email senders
├── sandbox/            Docker image, runner.py, and datamodel.py for running bot code
├── tests/              Unit tests and PostgreSQL-backed integration tests
├── Program.cs          Service setup, auth, CORS, and seed data
└── appsettings.json    Non-secret configuration
```

## Project status

**Working:** the price feed, the order book with market and limit orders, funds checks, ledger balances, magic-link sign-in, strategy versioning, the sandboxed bot runner, and candlestick charts.

**Known limitations**

- The API runs as a single instance. Running strategies are tracked in memory, so more than one API process would start duplicate runs.
- Trades settle immediately.
- The frontend doesn't yet show orders, positions, or submission logs.

## Future improvements

- Settlement delay modeling (T+1/T+2)
- Multi-symbol dashboard instead of a single-chart view, with orders, positions, and bot logs
- Leaderboard comparing bot performance
- Technicals, valuations, dividends, and profitability
