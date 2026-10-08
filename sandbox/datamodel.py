"""Types shared between the runner and strategy code.

A strategy imports what it needs:

    from datamodel import Order, State
"""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class Order:
    """An order for the strategy's account. Leave limit_price as None for a market order."""

    symbol: str
    side: str  # "BUY" or "SELL"
    quantity: int
    limit_price: float | None = None

    @staticmethod
    def buy(symbol: str, quantity: int, limit_price: float | None = None) -> Order:
        return Order(symbol, "BUY", quantity, limit_price)

    @staticmethod
    def sell(symbol: str, quantity: int, limit_price: float | None = None) -> Order:
        return Order(symbol, "SELL", quantity, limit_price)


@dataclass(frozen=True)
class OpenOrder:
    """One of the account's limit orders still resting on the book."""

    order_id: int
    symbol: str
    side: str
    quantity: float
    quantity_filled: float
    limit_price: float


@dataclass(frozen=True)
class OrderResult:
    """What happened to an order the strategy returned on the previous tick."""

    order_id: int | None  # None if the order was malformed and never placed
    symbol: str
    side: str
    status: str  # OPEN, PARTIAL, FILLED, CANCELLED, REJECTED
    quantity_filled: float
    reason: str | None


@dataclass(frozen=True)
class State:
    """A snapshot of the market and the account, taken just before on_tick is called."""

    timestamp: str
    prices: dict[str, float]  # latest price per symbol
    history: dict[str, list[float]]  # recent prices per symbol, oldest first
    cash: float
    available_cash: float  # cash minus what resting buy orders have reserved
    positions: dict[str, float]  # shares held per symbol
    open_orders: list[OpenOrder] = field(default_factory=list)
    last_results: list[OrderResult] = field(default_factory=list)
