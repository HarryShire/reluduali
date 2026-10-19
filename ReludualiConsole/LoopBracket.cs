using System;
using System.Globalization;
using System.Threading;
using BatchRecorder;

namespace ReludualiConsole
{
    /// <summary>
    /// The timeline's loop bracket, the global one (Mycelium 2026-10-18, 26:06 of his test take: "I want to be able to, on the
    /// bottom here, set loop brackets. I want to be able to say, keep playing section between here and here"; later the same
    /// day: "Make two separate ones that work together, because one may want to change the global bracket without changing
    /// the POV one ... the global one sort of like how Ableton loop brackets work").
    ///
    /// One bracket for the film, In to Out (film ticks), switched on and off like Ableton's loop switch. While it is on, the
    /// game keeps playing that section: when the film plays across Out from inside the bracket, it goes back to In by a
    /// RipperBridge keyframe jump (the snapshot the game keeps for a camera marker; one is taken at In the first time the film
    /// plays across it, so later loops land there). The POV takes' in and out are their own markers on the POV lane, and only
    /// change when asked ("Set from the loop"); this never touches them.
    ///
    /// The worker only jumps when it has watched the film play across Out from inside, so a seek, a scrub or a marker jump
    /// past Out is left alone; anything that drives the film by playing it (Apply to game) holds the loop meanwhile.
    /// </summary>
    public static class LoopBracket
    {
        static readonly object Gate = new object();
        static int _in = -1, _out = -1;
        static bool _on;
        static long _theater;
        static int _held;
        static Thread _worker;

        /// <summary>Raised on any thread when the bracket or its switch changes (the timeline redraws).</summary>
        public static event Action Changed;

        /// <summary>Said by the worker: where it is, and why it did not jump (for the timeline's text and the pipe).</summary>
        public static string Said { get; private set; }

        public static int In { get { return _in; } }
        public static int Out { get { return _out; } }
        public static bool On { get { return _on; } }
        public static bool Has { get { return _in >= 0 && _out > _in; } }
        public static int Loops { get; private set; }

        /// <summary>The bracket from <paramref name="a"/> to <paramref name="b"/> (either order). At least 100 ms long.</summary>
        public static void Set(int a, int b)
        {
            if (a < 0 || b < 0) return;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            if (hi - lo < 100) hi = lo + 100;
            lock (Gate) { _in = lo; _out = hi; }
            Raise();
        }

        public static void Clear() { lock (Gate) { _in = _out = -1; _on = false; } Raise(); }

        /// <summary>The loop switch. On with no bracket does nothing.</summary>
        public static void Switch(bool on)
        {
            lock (Gate)
            {
                if (on && !Has) return;
                if (_on == on) return;
                _on = on;
                if (on && (_worker == null || !_worker.IsAlive))
                {
                    _worker = new Thread(Work) { IsBackground = true, Name = "loop bracket", Priority = ThreadPriority.AboveNormal };
                    _worker.Start();
                }
            }
            Raise();
        }

        /// <summary>A new film: its bracket goes (film ticks of another film mean nothing here).</summary>
        public static void Film(long theater)
        {
            bool changed;
            lock (Gate) { changed = theater != 0 && _theater != 0 && theater != _theater; _theater = theater == 0 ? _theater : theater; }
            if (changed) Clear();
        }

        /// <summary>While something else drives the film by playing it (Apply to game, a recording), the loop waits.</summary>
        public static IDisposable Hold() { Interlocked.Increment(ref _held); return new Release(); }
        sealed class Release : IDisposable { int _done; public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _held); } }

        static void Raise() { Action a = Changed; if (a != null) try { a(); } catch { } }

        static int Tick()
        {
            string s = BridgeClient.Ask("stats", 300, 1);
            if (s == null || s.IndexOf("theater 0x00000000", StringComparison.Ordinal) >= 0) return int.MinValue;
            return (int)BridgeClient.Field(s, "tick");
        }

        static bool Busy()
        {
            if (Volatile.Read(ref _held) > 0) return true;
            if (BatchEngine.Recording || (BatchControl.Instance != null && BatchControl.Instance.IsBusy)) return true;
            TheaterTimeline tl = TheaterTimeline.Current;
            return tl != null && tl.Seeking;
        }

        /// <summary>The loop's own thread, while the switch is on.</summary>
        static void Work()
        {
            int prev = int.MinValue;
            long prevAt = 0;
            double rate = 1;                 // film ms per wall ms, measured
            bool first = true;
            bool savedAtIn = false;
            int lastIn = -1;
            Loops = 0;
            while (true)
            {
                int tin, tout;
                lock (Gate) { if (!_on) break; tin = _in; tout = _out; }
                if (tin != lastIn) { savedAtIn = false; lastIn = tin; }
                if (Busy()) { prev = int.MinValue; Thread.Sleep(100); continue; }
                int t;
                try { t = Tick(); } catch { t = int.MinValue; }
                long now = Environment.TickCount;
                if (t == int.MinValue) { Said = "no film"; prev = int.MinValue; Thread.Sleep(250); continue; }

                // switched on outside the bracket: to its start, then play
                if (first)
                {
                    first = false;
                    if (t < tin - 200 || t > tout)
                    {
                        Said = "going to the loop's start";
                        if (GoTo(tin)) savedAtIn = true;
                        BridgeClient.Ask("cmd demo_pause 0", 800, 1);
                        prev = int.MinValue;
                        continue;
                    }
                }

                if (prev != int.MinValue && t != prev && now > prevAt)
                {
                    double r = (t - prev) / (double)(now - prevAt);
                    if (r > 0 && r < 20) rate = rate * 0.6 + r * 0.4;
                }
                bool playing = prev != int.MinValue && t > prev && t - prev < 2000;

                // a keyframe just before the loop's start, the first time the film plays up to it (later loops land there). A
                // keyframe lands on the next snapshot after the moment it is taken (0732: taken at 44008, it landed at 44050),
                // and the jump takes the last one at or before the start: so it is taken 55-130 ms before (0733: from 150 ms
                // before, the loops landed 150 ms early)
                if (playing && !savedAtIn && t >= tin - 130 && t <= tin - 55)
                {
                    string r = BridgeClient.Ask("kf save", 3000, 1);
                    savedAtIn = r != null && (r.StartsWith("ok", StringComparison.Ordinal) || r.StartsWith("have", StringComparison.Ordinal));
                }

                // played across the end from inside: back to the start
                if (playing && prev >= tin - 400 && prev < tout && t >= tout)
                {
                    string r = BridgeClient.Ask("kf jump " + tin.ToString(CultureInfo.InvariantCulture), 10000, 1);
                    if (r != null && r.StartsWith("ok", StringComparison.Ordinal))
                    {
                        Loops++;
                        Said = "loop " + Loops;
                        BridgeClient.Ask("cmd demo_pause 0", 800, 1);       // a jump keeps the film playing; this makes sure
                    }
                    else
                    {
                        // no keyframe at or before the start (the film never played there in this load): the timeline's seek
                        Said = "back to the loop's start the long way (" + (r ?? "no answer") + ")";
                        if (GoTo(tin)) savedAtIn = true;
                        BridgeClient.Ask("cmd demo_pause 0", 800, 1);
                    }
                    prev = int.MinValue;
                    Raise();
                    continue;
                }

                prev = t; prevAt = now;
                // poll faster as the next thing comes near (the keyframe's window before the start, then the end): a few ms of
                // film late at most
                int wait = 120;
                int next = !savedAtIn && t < tin - 55 ? tin - 95 : tout;
                if (playing && t < next) wait = (int)Math.Max(5, Math.Min(120, (next - t) / Math.Max(0.05, rate) / 3));
                Thread.Sleep(wait);
            }
            Said = "off";
        }

        /// <summary>The film to just before a loop's start, and a keyframe taken there (the seek plays past the window where
        /// the worker takes it: 0734's first loop landed on an older keyframe, 1.6 s early). True when the keyframe was kept.</summary>
        static bool GoTo(int tick)
        {
            int at = tick - 90;
            TheaterTimeline tl = TheaterTimeline.Current;
            if (tl == null || !tl.SeekNow(at)) BridgeClient.Ask("kf jump " + at.ToString(CultureInfo.InvariantCulture), 10000, 1);
            string r = BridgeClient.Ask("kf save", 3000, 1);
            return r != null && (r.StartsWith("ok", StringComparison.Ordinal) || r.StartsWith("have", StringComparison.Ordinal));
        }
    }
}
