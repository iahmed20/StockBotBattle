"""Runs one strategy inside the sandbox container.

The API talks to this process over stdin/stdout, one JSON message per line:

    -> {"type": "init", "code": "<strategy source>"}
    <- {"type": "ready"}  or  {"type": "error", "message": "...", "logs": "..."}

    -> {"type": "tick", "state": {...}}         (fields of datamodel.State)
    <- {"type": "orders", "orders": [...], "logs": "..."}

The strategy defines on_tick(symbol, price) or on_tick(symbol, price, state), which is called
once per symbol on every tick and may return an Order, a list of Orders, or None.
Anything the strategy prints is captured and sent back as logs, never written to stdout.
"""
import contextlib
import inspect
import io
import json
import linecache
import math
import os
import sys
import traceback
import types

from datamodel import OpenOrder, Order, OrderResult, State

MAX_LOG_CHARS = 4000
STRATEGY_FILE = "strategy.py"


class Protocol:
    def __init__(self):
        # Keep a private copy of stdout for protocol messages, then point fd 1 at /dev/null
        # so nothing the strategy writes (even from C extensions) can corrupt the stream.
        self._out = os.fdopen(os.dup(1), "w", encoding="utf-8")
        devnull = os.open(os.devnull, os.O_WRONLY)
        os.dup2(devnull, 1)
        os.close(devnull)

    def send(self, message: dict) -> None:
        self._out.write(json.dumps(message, allow_nan=False) + "\n")
        self._out.flush()


def capture():
    """Redirects print() and sys.stderr into a buffer for the duration of a call."""
    buffer = io.StringIO()
    stack = contextlib.ExitStack()
    stack.enter_context(contextlib.redirect_stdout(buffer))
    stack.enter_context(contextlib.redirect_stderr(buffer))
    return stack, buffer


def trim(logs: str) -> str:
    if len(logs) <= MAX_LOG_CHARS:
        return logs
    return logs[: MAX_LOG_CHARS - 40] + f"\n... ({len(logs) - MAX_LOG_CHARS + 40} more characters dropped)\n"


def user_traceback(exc: BaseException) -> str:
    """Formats an exception, keeping only the frames from the strategy's own code."""
    tb = exc.__traceback__
    while tb is not None and tb.tb_frame.f_code.co_filename != STRATEGY_FILE:
        tb = tb.tb_next
    return "".join(traceback.format_exception(type(exc), exc, tb))


def load_strategy(code: str):
    module = types.ModuleType("strategy")
    module.__file__ = STRATEGY_FILE
    sys.modules["strategy"] = module
    linecache.cache[STRATEGY_FILE] = (len(code), None, code.splitlines(True), STRATEGY_FILE)  # source lines in tracebacks
    exec(compile(code, STRATEGY_FILE, "exec"), module.__dict__)

    on_tick = getattr(module, "on_tick", None)
    if not callable(on_tick):
        raise ValueError("strategy.py must define a function on_tick(symbol, price, state=None).")

    params = inspect.signature(on_tick).parameters.values()
    takes_varargs = any(p.kind == p.VAR_POSITIONAL for p in params)
    positional = [p for p in params if p.kind in (p.POSITIONAL_ONLY, p.POSITIONAL_OR_KEYWORD)]
    if takes_varargs or len(positional) >= 3:
        return on_tick
    if len(positional) == 2:
        return lambda symbol, price, state: on_tick(symbol, price)
    raise ValueError("on_tick must take (symbol, price) or (symbol, price, state).")


def parse_state(raw: dict) -> State:
    return State(
        timestamp=raw["timestamp"],
        prices=raw["prices"],
        history=raw["history"],
        cash=raw["cash"],
        available_cash=raw["available_cash"],
        positions=raw["positions"],
        open_orders=[OpenOrder(**o) for o in raw["open_orders"]],
        last_results=[OrderResult(**r) for r in raw["last_results"]],
    )


def to_message(order) -> dict:
    """Checks an order returned by the strategy and converts it for the API."""
    if not isinstance(order, Order):
        raise TypeError(f"on_tick returned {type(order).__name__}; return Order objects (from datamodel import Order).")

    quantity = order.quantity
    if isinstance(quantity, float) and quantity.is_integer():
        quantity = int(quantity)
    if isinstance(quantity, bool) or not isinstance(quantity, int):
        raise ValueError(f"Order quantity must be a whole number, got {order.quantity!r}.")

    limit = order.limit_price
    if limit is not None:
        if isinstance(limit, bool) or not isinstance(limit, (int, float)) or not math.isfinite(limit):
            raise ValueError(f"Order limit_price must be a finite number or None, got {limit!r}.")
        limit = float(limit)

    return {
        "symbol": str(order.symbol),
        "side": str(order.side).upper(),
        "quantity": quantity,
        "order_type": "MARKET" if limit is None else "LIMIT",
        "limit_price": limit,
    }


def run_tick(on_tick, state: State) -> tuple[list[dict], str]:
    orders: list[dict] = []
    stack, logs = capture()
    with stack:
        for symbol, price in state.prices.items():
            try:
                result = on_tick(symbol, price, state)
                if result is None:
                    continue
                for order in result if isinstance(result, (list, tuple)) else [result]:
                    orders.append(to_message(order))
            except Exception as exc:  # a bug in one call shouldn't stop the bot
                print(f"Error in on_tick for {symbol}:\n{user_traceback(exc)}", end="")
    return orders, logs.getvalue()


def main() -> None:
    protocol = Protocol()
    on_tick = None

    for line in sys.stdin:
        message = json.loads(line)

        if message["type"] == "init":
            stack, logs = capture()
            try:
                with stack:
                    on_tick = load_strategy(message["code"])
            except BaseException as exc:  # includes SyntaxError and SystemExit from strategy code
                protocol.send({"type": "error", "message": user_traceback(exc).strip(), "logs": trim(logs.getvalue())})
                return
            protocol.send({"type": "ready", "logs": trim(logs.getvalue())})

        elif message["type"] == "tick":
            if on_tick is None:
                protocol.send({"type": "error", "message": "Received a tick before init."})
                return
            orders, logs = run_tick(on_tick, parse_state(message["state"]))
            protocol.send({"type": "orders", "orders": orders, "logs": trim(logs)})


if __name__ == "__main__":
    main()
