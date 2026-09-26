using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditSharp
{
    /// <summary>Which way a conversion to whole frames or samples rounds.</summary>
    public enum Rounding
    {
        /// <summary>Toward negative infinity: the frame or sample in progress at the time.</summary>
        Floor,

        /// <summary>Toward positive infinity: the first frame or sample at or after the time.</summary>
        Ceiling,

        /// <summary>To the nearest, halves away from zero.</summary>
        Nearest,
    }

    /// <summary>A moment or a length of time, as a whole number of ticks.</summary>
    /// <remarks>
    /// A tick is 1/254,016,000,000 of a second, so every common frame rate (including
    /// 30000/1001) and every common sample rate is a whole number of ticks per frame or
    /// sample. A long holds about 420 days of ticks. Arithmetic is checked and throws
    /// <see cref="OverflowException"/>, as <see cref="TimeSpan"/> does.
    /// </remarks>
    /// <param name="Ticks">The number of ticks.</param>
    [JsonConverter(typeof(TimeJsonConverter))]
    public readonly record struct Time(long Ticks) : IComparable<Time>, IComparable
    {
        /// <summary>Ticks in one second.</summary>
        public const long TicksPerSecond = 254_016_000_000;

        /// <summary>Ticks in one millisecond.</summary>
        public const long TicksPerMillisecond = TicksPerSecond / 1000;

        /// <summary>No time.</summary>
        public static Time Zero => default;

        /// <summary>The shortest positive time: one tick.</summary>
        public static Time Tick => new(1);

        /// <summary>The latest time; also used for "no limit".</summary>
        public static Time MaxValue => new(long.MaxValue);

        /// <summary>The earliest time; also used for "no limit".</summary>
        public static Time MinValue => new(long.MinValue);

        /// <summary>The time in seconds, as the nearest double; for display and signal processing.</summary>
        public double Seconds => Ticks / (double)TicksPerSecond;

        /// <summary>The time in milliseconds, as the nearest double; for display.</summary>
        public double Milliseconds => Ticks / (double)TicksPerMillisecond;

        /// <summary>A whole number of seconds, exactly.</summary>
        /// <param name="seconds">The seconds.</param>
        /// <returns>The time.</returns>
        public static Time FromSeconds(long seconds) => new(checked(seconds * TicksPerSecond));

        /// <summary>Seconds, rounded to the nearest tick.</summary>
        /// <param name="seconds">The seconds.</param>
        /// <returns>The time.</returns>
        /// <exception cref="OverflowException">The time doesn't fit, or isn't finite.</exception>
        public static Time FromSeconds(double seconds) => new(RoundToLong(seconds * TicksPerSecond));

        /// <summary>A fraction of seconds, rounded to the nearest tick.</summary>
        /// <param name="seconds">The seconds.</param>
        /// <returns>The time.</returns>
        public static Time FromSeconds(Rational seconds) => new(MulDiv(TicksPerSecond, seconds.Num, seconds.Den, Rounding.Nearest));

        /// <summary>A whole number of milliseconds, exactly.</summary>
        /// <param name="milliseconds">The milliseconds.</param>
        /// <returns>The time.</returns>
        public static Time FromMilliseconds(long milliseconds) => new(checked(milliseconds * TicksPerMillisecond));

        /// <summary>Milliseconds, rounded to the nearest tick.</summary>
        /// <param name="milliseconds">The milliseconds.</param>
        /// <returns>The time.</returns>
        /// <exception cref="OverflowException">The time doesn't fit, or isn't finite.</exception>
        public static Time FromMilliseconds(double milliseconds) => new(RoundToLong(milliseconds * TicksPerMillisecond));

        /// <summary>A <see cref="TimeSpan"/>, for times from .NET such as a stopwatch's.</summary>
        /// <param name="span">The span.</param>
        /// <returns>The time, rounded to the nearest tick.</returns>
        public static Time FromTimeSpan(TimeSpan span) => new(MulDiv(span.Ticks, TicksPerSecond, TimeSpan.TicksPerSecond, Rounding.Nearest));

        /// <summary>As a <see cref="TimeSpan"/>, for .NET calls such as <see cref="System.Threading.Tasks.Task.Delay(TimeSpan)"/>; rounded to its 100 ns ticks.</summary>
        /// <returns>The span; <see cref="TimeSpan.MaxValue"/> or <see cref="TimeSpan.MinValue"/> for this type's limits.</returns>
        public TimeSpan ToTimeSpan()
        {
            if (Ticks == long.MaxValue) return TimeSpan.MaxValue;
            if (Ticks == long.MinValue) return TimeSpan.MinValue;
            return TimeSpan.FromTicks(MulDiv(Ticks, TimeSpan.TicksPerSecond, TicksPerSecond, Rounding.Nearest));
        }

        /// <summary>As a timeout for .NET waits such as <see cref="System.Threading.Tasks.Task.Wait(TimeSpan)"/>: <see cref="MaxValue"/> waits forever, and a negative time doesn't wait.</summary>
        /// <returns>The timeout.</returns>
        public TimeSpan ToTimeout()
        {
            if (Ticks == long.MaxValue) return System.Threading.Timeout.InfiniteTimeSpan;
            if (Ticks <= 0) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(Math.Min(ToTimeSpan().TotalMilliseconds, int.MaxValue - 1));
        }

        /// <summary>When a frame starts at a frame rate, rounded to the nearest tick.</summary>
        /// <param name="frame">The frame index.</param>
        /// <param name="fps">Frames per second; positive.</param>
        /// <returns>The time.</returns>
        public static Time FromFrame(long frame, Rational fps) => new(Divide((Int128)frame * TicksPerSecond * fps.Den, fps.Num, Rounding.Nearest));

        /// <summary>The frame at this time at a frame rate.</summary>
        /// <param name="fps">Frames per second; positive.</param>
        /// <param name="rounding">Floor gives the frame showing at this time.</param>
        /// <returns>The frame index.</returns>
        public long ToFrame(Rational fps, Rounding rounding = Rounding.Floor) => Divide((Int128)Ticks * fps.Num, (Int128)TicksPerSecond * fps.Den, rounding);

        /// <summary>The length of one frame at a frame rate.</summary>
        /// <param name="fps">Frames per second; positive.</param>
        /// <returns>The length, rounded to the nearest tick; exact for every common rate.</returns>
        public static Time FrameLength(Rational fps) => FromFrame(1, fps);

        /// <summary>When a sample starts at a sample rate.</summary>
        /// <param name="sample">The sample index.</param>
        /// <param name="sampleRate">Samples per second; positive.</param>
        /// <returns>The time, rounded to the nearest tick; exact for every common rate.</returns>
        public static Time FromSamples(long sample, long sampleRate) => new(MulDiv(sample, TicksPerSecond, sampleRate, Rounding.Nearest));

        /// <summary>The sample at this time at a sample rate.</summary>
        /// <param name="sampleRate">Samples per second; positive.</param>
        /// <param name="rounding">Floor gives the sample in progress at this time.</param>
        /// <returns>The sample index.</returns>
        public long ToSamples(long sampleRate, Rounding rounding = Rounding.Floor) => MulDiv(Ticks, sampleRate, TicksPerSecond, rounding);

        /// <summary>Snaps to the frame grid of a frame rate.</summary>
        /// <param name="fps">Frames per second; positive.</param>
        /// <param name="rounding">Which way to snap.</param>
        /// <returns>The start of that frame.</returns>
        public Time SnapToFrame(Rational fps, Rounding rounding = Rounding.Nearest) => FromFrame(ToFrame(fps, rounding), fps);

        /// <summary>This time multiplied by a fraction, rounded to the nearest tick.</summary>
        /// <param name="factor">The fraction.</param>
        /// <param name="rounding">Which way to round.</param>
        /// <returns>The scaled time.</returns>
        public Time Scale(Rational factor, Rounding rounding = Rounding.Nearest) => new(MulDiv(Ticks, factor.Num, factor.Den, rounding));

        /// <summary>This time multiplied by a double, rounded to the nearest tick; for rates that aren't exact, such as a playback shuttle speed.</summary>
        /// <param name="factor">The factor.</param>
        /// <returns>The scaled time.</returns>
        /// <exception cref="OverflowException">The result doesn't fit, or isn't finite.</exception>
        public Time Scale(double factor) => new(RoundToLong(Ticks * factor));

        /// <summary>The length of this time with its sign dropped.</summary>
        /// <returns>The absolute time.</returns>
        public Time Abs() => new(Math.Abs(Ticks));

        /// <summary>The earlier of two times.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>The smaller.</returns>
        public static Time Min(Time a, Time b) => a.Ticks <= b.Ticks ? a : b;

        /// <summary>The later of two times.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>The larger.</returns>
        public static Time Max(Time a, Time b) => a.Ticks >= b.Ticks ? a : b;

        /// <summary>A time held inside a range.</summary>
        /// <param name="value">The time.</param>
        /// <param name="min">The earliest allowed.</param>
        /// <param name="max">The latest allowed.</param>
        /// <returns>The clamped time.</returns>
        public static Time Clamp(Time value, Time min, Time max) => value < min ? min : value > max ? max : value;

        /// <summary>The sum of two times.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>a + b.</returns>
        /// <exception cref="OverflowException">The sum doesn't fit.</exception>
        public static Time operator +(Time a, Time b) => new(checked(a.Ticks + b.Ticks));

        /// <summary>The difference of two times.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>a - b.</returns>
        /// <exception cref="OverflowException">The difference doesn't fit.</exception>
        public static Time operator -(Time a, Time b) => new(checked(a.Ticks - b.Ticks));

        /// <summary>The negated time.</summary>
        /// <param name="a">The time.</param>
        /// <returns>-a.</returns>
        public static Time operator -(Time a) => new(checked(-a.Ticks));

        /// <summary>A time repeated a whole number of times.</summary>
        /// <param name="a">The time.</param>
        /// <param name="n">The count.</param>
        /// <returns>a × n.</returns>
        public static Time operator *(Time a, long n) => new(checked(a.Ticks * n));

        /// <summary>A time repeated a whole number of times.</summary>
        /// <param name="n">The count.</param>
        /// <param name="a">The time.</param>
        /// <returns>n × a.</returns>
        public static Time operator *(long n, Time a) => new(checked(a.Ticks * n));

        /// <summary>A time multiplied by a fraction, rounded to the nearest tick.</summary>
        /// <param name="a">The time.</param>
        /// <param name="factor">The fraction.</param>
        /// <returns>a × factor.</returns>
        public static Time operator *(Time a, Rational factor) => a.Scale(factor);

        /// <summary>A time divided by a fraction, rounded to the nearest tick.</summary>
        /// <param name="a">The time.</param>
        /// <param name="divisor">The fraction; not zero.</param>
        /// <returns>a ÷ divisor.</returns>
        public static Time operator /(Time a, Rational divisor) => a.Scale(divisor.Reciprocal());

        /// <summary>A time divided into a whole number of parts, rounded toward zero.</summary>
        /// <param name="a">The time.</param>
        /// <param name="n">The number of parts.</param>
        /// <returns>a ÷ n.</returns>
        public static Time operator /(Time a, long n) => new(a.Ticks / n);

        /// <summary>How many times one time fits in another, as a double; for display and progress.</summary>
        /// <param name="a">The dividend.</param>
        /// <param name="b">The divisor.</param>
        /// <returns>a ÷ b.</returns>
        public static double operator /(Time a, Time b) => (double)a.Ticks / b.Ticks;

        /// <summary>What's left over after dividing one time by another.</summary>
        /// <param name="a">The dividend.</param>
        /// <param name="b">The divisor.</param>
        /// <returns>a mod b, with the sign of a.</returns>
        public static Time operator %(Time a, Time b) => new(a.Ticks % b.Ticks);

        /// <summary>Whether a is earlier than b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is earlier.</returns>
        public static bool operator <(Time a, Time b) => a.Ticks < b.Ticks;

        /// <summary>Whether a is later than b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is later.</returns>
        public static bool operator >(Time a, Time b) => a.Ticks > b.Ticks;

        /// <summary>Whether a is at or before b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is earlier or equal.</returns>
        public static bool operator <=(Time a, Time b) => a.Ticks <= b.Ticks;

        /// <summary>Whether a is at or after b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is later or equal.</returns>
        public static bool operator >=(Time a, Time b) => a.Ticks >= b.Ticks;

        /// <inheritdoc/>
        public int CompareTo(Time other) => Ticks.CompareTo(other.Ticks);

        /// <inheritdoc/>
        public int CompareTo(object? obj) => obj is Time other ? CompareTo(other)
            : obj is null ? 1 : throw new ArgumentException("Only a Time compares with a Time.", nameof(obj));

        /// <summary>"[-]h:mm:ss.ffffff", in seconds to the microsecond.</summary>
        /// <returns>The text.</returns>
        public override string ToString()
        {
            if (Ticks == long.MaxValue) return "max";
            if (Ticks == long.MinValue) return "min";

            long micros = MulDiv(Math.Abs(Ticks), 1_000_000, TicksPerSecond, Rounding.Nearest);
            string sign = Ticks < 0 ? "-" : "";
            long seconds = micros / 1_000_000;
            return string.Create(CultureInfo.InvariantCulture,
                $"{sign}{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}.{micros % 1_000_000:000000}");
        }

        /// <summary>a × b ÷ c without overflowing in between, rounded as asked.</summary>
        /// <param name="a">The value.</param>
        /// <param name="b">The multiplier.</param>
        /// <param name="c">The divisor; not zero.</param>
        /// <param name="rounding">Which way to round.</param>
        /// <returns>The result.</returns>
        /// <exception cref="OverflowException">The result doesn't fit a long.</exception>
        public static long MulDiv(long a, long b, long c, Rounding rounding) => Divide((Int128)a * b, c, rounding);

        //n ÷ d rounded as asked, checked into a long
        private static long Divide(Int128 n, Int128 d, Rounding rounding)
        {
            if (d == 0) throw new DivideByZeroException();
            if (d < 0) { n = -n; d = -d; }

            (Int128 q, Int128 r) = Int128.DivRem(n, d);
            if (r != 0)
            {
                q += rounding switch
                {
                    Rounding.Floor => n < 0 ? -1 : 0,
                    Rounding.Ceiling => n > 0 ? 1 : 0,
                    _ => Int128.Abs(r) * 2 >= d ? (n < 0 ? -1 : 1) : 0,
                };
            }

            return checked((long)q);
        }

        private static long RoundToLong(double value)
        {
            double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (!double.IsFinite(rounded) || rounded >= 9.2233720368547758e18 || rounded < -9.2233720368547758e18)
                throw new OverflowException("The time doesn't fit.");
            return (long)rounded;
        }
    }

    //a Time in JSON: its tick count
    internal sealed class TimeJsonConverter : JsonConverter<Time>
    {
        public override Time Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetInt64());

        public override void Write(Utf8JsonWriter writer, Time value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Ticks);
    }
}
