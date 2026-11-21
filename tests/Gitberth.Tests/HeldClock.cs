namespace Gitberth.Tests;

/// <summary>
/// A clock that holds every wait the code starts on it until the test lets it go. Task.Delay(…, clock, token) makes one
/// timer per wait and disposes it when its token is cancelled: each is listed here, in the order it was started, with how
/// long it was for (<see cref="Wait.Due"/>), and fires only once <see cref="Advance"/> moves the clock past its time. A
/// test then asserts a retry schedule exactly, wait by wait, and changes things while the code is parked in a wait,
/// instead of sleeping through it and hoping the code is where it thinks. Time stands still otherwise: nothing here
/// depends on how busy the machine is. <see cref="FakeClock"/> is for "every wait passes at once".
/// </summary>
internal sealed class HeldClock : TimeProvider
{
    /// <summary>How long <see cref="NextWaitAsync"/> waits for the code to start a wait: a guard against a hang, never what a passing test waits.</summary>
    public static readonly TimeSpan HangGuard = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly List<Wait> _waits = [];
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private int _taken;
    private TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    // Elapsed time as the clock has it, so a Stopwatch-style measure on this clock moves only when it does.
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    /// <summary>Every wait started so far, in order.</summary>
    public IReadOnlyList<Wait> Waits
    {
        get
        {
            lock (_gate) return [.. _waits];
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var wait = new Wait(this, callback, state, _waits.Count + 1, dueTime, period, _now);
            _waits.Add(wait);
            var started = _started;
            _started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            started.TrySetResult();
            return wait;
        }
    }

    /// <summary>
    /// The next wait the code starts after the last one this returned (or <see cref="ReleaseAll"/> let go), once it's
    /// started: the code is parked in it then, and does nothing more until it fires or is cancelled.
    /// </summary>
    public async Task<Wait> NextWaitAsync()
    {
        var until = DateTime.UtcNow + HangGuard;
        while (true)
        {
            Task started;
            lock (_gate)
            {
                if (_taken < _waits.Count) return _waits[_taken++];
                started = _started.Task;
            }
            var left = until - DateTime.UtcNow;
            try
            {
                await started.WaitAsync(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            catch (TimeoutException)
            {
                Assert.Fail($"The code started no wait in {HangGuard.TotalSeconds:0} s (wait {_taken + 1} was expected; {Describe()}).");
            }
        }
    }

    /// <summary>
    /// Moves the clock on, and fires (on the thread pool, as a timer does) every wait whose time has come. A periodic timer
    /// (a PeriodicTimer on this clock) fires once per call, however many periods went by.
    /// </summary>
    public void Advance(TimeSpan by)
    {
        List<Wait> due;
        lock (_gate)
        {
            _now += by;
            due = _waits.Where(w => w.TakeIfDue(_now)).OrderBy(w => w.At).ThenBy(w => w.Number).ToList();
        }
        foreach (var wait in due) ThreadPool.UnsafeQueueUserWorkItem(static w => w.Fire(), wait, preferLocal: false);
    }

    /// <summary>
    /// Lets every wait started so far go: the clock moves on to the latest of their times. For a test that only needs the
    /// code to get on with it; <see cref="NextWaitAsync"/> then returns the waits started after this.
    /// </summary>
    public void ReleaseAll()
    {
        TimeSpan by;
        lock (_gate)
        {
            _taken = _waits.Count;
            // A wait for ever (Timeout.InfiniteTimeSpan) never comes, here as anywhere.
            var latest = _waits.Where(w => w.Held && w.At != DateTimeOffset.MaxValue).Select(w => w.At).DefaultIfEmpty(_now).Max();
            by = latest > _now ? latest - _now : TimeSpan.Zero;
        }
        Advance(by);
    }

    private string Describe()
    {
        lock (_gate)
            return _waits.Count == 0 ? "none was started" : string.Join(", ", _waits.Select(w => w.ToString()));
    }

    /// <summary>One wait: how long it was for, and whether it fired or was cut short.</summary>
    public sealed class Wait : ITimer
    {
        private readonly HeldClock _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private TimeSpan _period;
        private bool _fired, _disposed, _firing;

        internal Wait(HeldClock clock, TimerCallback callback, object? state, int number, TimeSpan due, TimeSpan period, DateTimeOffset now)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            Number = number;
            Due = due;
            _period = period;
            At = due == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + due;
        }

        /// <summary>Which wait it is: 1 for the first one the code started.</summary>
        public int Number { get; }

        /// <summary>How long it was for, as the code asked.</summary>
        public TimeSpan Due { get; private set; }

        /// <summary>When it's due, on the clock.</summary>
        public DateTimeOffset At { get; private set; }

        /// <summary>Its time came and it fired.</summary>
        public bool Fired
        {
            get
            {
                lock (_clock._gate) return _fired;
            }
        }

        /// <summary>Disposed before its time: Task.Delay does that when its token is cancelled.</summary>
        public bool Cancelled
        {
            get
            {
                lock (_clock._gate) return _disposed && !_fired;
            }
        }

        // Under the clock's lock: a periodic timer comes round again after it fired; Task.Delay's never does.
        private bool Periodic => _period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero;

        /// <summary>Neither fired nor cancelled yet (a periodic one: not cancelled): the code is waiting on it. Under the clock's lock.</summary>
        internal bool Held => !_disposed && !_firing && (!_fired || Periodic);

        // Under the clock's lock: whether it fires now (its time has come), marking it so it fires once.
        internal bool TakeIfDue(DateTimeOffset now)
        {
            if (!Held || At > now) return false;
            _firing = true;
            return true;
        }

        internal void Fire()
        {
            lock (_clock._gate)
            {
                _firing = false;
                if (_disposed) return;
                _fired = true;
                if (Periodic) At += _period;
            }
            _callback(_state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_clock._gate)
            {
                if (_disposed) return false;
                Due = dueTime;
                _period = period;
                _fired = false;
                At = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : _clock._now + dueTime;
                return true;
            }
        }

        public void Dispose()
        {
            lock (_clock._gate) _disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public override string ToString()
        {
            lock (_clock._gate)
                return $"wait {Number} of {Due.TotalMilliseconds:0} ms ({(_fired ? "fired" : _disposed ? "cancelled" : "held")})";
        }
    }
}
