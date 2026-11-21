# Contributing to Gitberth

Bug reports, ideas and pull requests are welcome.

Gitberth is free for personal and noncommercial use under the [PolyForm Noncommercial License 1.0.0](LICENSE.md), and ObjectState licenses it to businesses. So that a contribution can ship in both, by submitting one (a pull request, a patch, or code in an issue) you agree that:

1. You wrote it, or otherwise have the right to submit it under these terms.
2. You grant ObjectState a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license to use, copy, modify, distribute and sublicense your contribution, under the PolyForm Noncommercial License, a commercial license or any other terms, along with a license to any of your patent claims that the contribution covers.
3. You keep the copyright in your contribution.

Questions about licensing go to business@objectstate.com.

## Testing a change

- `dotnet test tests/Gitberth.Tests` runs the unit tests. GitHub Actions runs them on every push and pull request.
- The end-to-end tests run Gitberth in a Docker engine of its own against stand-ins for GitHub and Cloudflare. Testing a
  change takes 10 minutes in all: on a Docker host that allows privileged containers, `sh tests/e2e/run.sh impact`
  runs the unit tests, then the scenario tests your change touches and a short smoke, as many as fit, and lists what
  it left for the full suite. `sh tests/e2e/run.sh plan` shows which, and why. See tests/e2e/README.md.
- Each pull request's "E2E impact" check says which tests it needs. The whole suite is on demand, before a release:
  Actions › E2E (or a pushed `e2e-*` tag) runs it in about 25 minutes.
- Moving code or adding a feature area may need a rule in `tests/e2e/impact.toml`; `python3 tests/e2e/impact.py check`
  checks the map.

### Waiting in tests

A test that sleeps a fixed time and then asserts fails on a busy machine and wastes time on an idle one. So:

- Never assert after a fixed sleep. Wait on what you're waiting for: an event (`AuditWaits.RecordedAsync` for an audit
  entry, a task's `WaitAsync`), or a condition with `Eventually.UntilAsync`, which polls every 25 ms. Their limits
  (a minute) only guard against a hang: a passing test never waits them out.
- Play retry schedules and timers with a clock: `HeldClock` holds each wait until the test lets it go, so the schedule
  is asserted wait by wait and things change while the code is parked; `FakeClock` lets every wait pass at once.
- A short sleep is fine only to show that something *doesn't* happen. `WaitingTests` fails on a unit test that sleeps a
  fixed second or more.
- In the end-to-end scenarios, every wait goes through `e2e.wait.until`, and a check of timing asserts what caused it,
  never how many seconds it took (see tests/e2e/README.md).
