"""Polling with a timeout. Every wait in the scenarios goes through until(): it never sleeps longer than it must, and when
it gives up it says what it was waiting for and what it saw last."""
import json
import time


class TimedOut(AssertionError):
    pass


def _show(value, limit=1500):
    try:
        text = value if isinstance(value, str) else json.dumps(value, default=str, indent=None)
    except (TypeError, ValueError):
        text = repr(value)
    return text if len(text) <= limit else text[:limit] + "…"


def until(check, timeout, what, interval=1.0, seen=None):
    """Calls check() until it returns something truthy, and returns that. Exceptions count as "not yet". After timeout
    seconds it fails with `what` and the last thing seen: seen() when given, else check()'s last result or error."""
    deadline = time.monotonic() + timeout
    last, error = None, None
    while True:
        try:
            last, error = check(), None
            if last:
                return last
        except Exception as e:  # noqa: BLE001 - anything but success is "not yet"
            error = e
        if time.monotonic() >= deadline:
            if seen is not None:
                try:
                    shown = _show(seen())
                except Exception as e:  # noqa: BLE001
                    shown = f"(couldn't read it: {e})"
            else:
                shown = f"{type(error).__name__}: {error}" if error is not None else _show(last)
            raise TimedOut(f"Timed out after {timeout:.0f}s waiting for {what}. Last seen: {shown}")
        time.sleep(interval)


def stays(check, seconds, what, interval=1.0):
    """Asserts check() stays truthy for the whole time (something that must NOT happen keeps not happening)."""
    deadline = time.monotonic() + seconds
    while True:
        result = check()
        assert result, f"{what} stopped holding: {_show(result)}"
        if time.monotonic() >= deadline:
            return result
        time.sleep(interval)
