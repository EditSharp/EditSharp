using System;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EditSharp
{
    /// <summary>An exact fraction of two longs, always reduced with a positive denominator.</summary>
    /// <remarks>Used for frame rates and clip speeds, so 30000/1001 fps and a 3/2 speed stay exact.</remarks>
    [JsonConverter(typeof(RationalJsonConverter))]
    public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>, IComparable
    {
        /// <summary>The numerator; carries the sign.</summary>
        public long Num { get; }

        readonly long _den;
        /// <summary>The denominator; always positive, and 1 for a default instance.</summary>
        public long Den => _den == 0 ? 1 : _den;

        /// <summary>A fraction, reduced.</summary>
        /// <param name="num">The numerator.</param>
        /// <param name="den">The denominator; not zero.</param>
        /// <exception cref="DivideByZeroException"><paramref name="den"/> is zero.</exception>
        public Rational(long num, long den)
        {
            if (den == 0) throw new DivideByZeroException("A rational's denominator can't be zero.");

            if (den < 0) { num = -num; den = -den; }
            long gcd = (long)BigInteger.GreatestCommonDivisor(num, den);
            if (gcd > 1) { num /= gcd; den /= gcd; }

            Num = num;
            _den = den;
        }

        /// <summary>A whole number.</summary>
        /// <param name="value">The value.</param>
        public Rational(long value) : this(value, 1) { }

        /// <summary>0/1.</summary>
        public static Rational Zero => new(0, 1);

        /// <summary>1/1.</summary>
        public static Rational One => new(1, 1);

        /// <summary>The nearest double, for display and signal processing.</summary>
        public double Value => (double)Num / Den;

        /// <summary>Whether it's above zero.</summary>
        public bool IsPositive => Num > 0;

        /// <summary>One over this.</summary>
        /// <returns>Den/Num.</returns>
        /// <exception cref="DivideByZeroException">This is zero.</exception>
        public Rational Reciprocal() => new(Den, Num);

        /// <summary>A decimal as an exact fraction: 150.25 is 601/4.</summary>
        /// <param name="value">The value.</param>
        /// <returns>The fraction.</returns>
        /// <exception cref="OverflowException">The value has too many digits for a long fraction.</exception>
        public static Rational FromDecimal(decimal value)
        {
            int scale = (int)((decimal.GetBits(value)[3] >> 16) & 0xFF);
            long den = 1;
            for (int i = 0; i < scale; i++) den = checked(den * 10);
            return new Rational(decimal.ToInt64(value * den), den);
        }

        /// <summary>The closest fraction to a double with a denominator no larger than a limit, by continued fractions.</summary>
        /// <remarks>29.97002997 with a limit of 100000 gives 30000/1001.</remarks>
        /// <param name="value">The value.</param>
        /// <param name="maxDenominator">The largest denominator allowed.</param>
        /// <returns>The fraction.</returns>
        /// <exception cref="ArgumentException"><paramref name="value"/> isn't finite.</exception>
        public static Rational Approximate(double value, long maxDenominator = 100000)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("Only a finite value has a fraction.", nameof(value));

            bool negative = value < 0;
            double x = Math.Abs(value);

            long h0 = 0, h1 = 1, k0 = 1, k1 = 0;
            double rest = x;
            for (int i = 0; i < 64; i++)
            {
                double whole = Math.Floor(rest);
                if (whole > long.MaxValue / 2) break;
                long a = (long)whole;

                long h2 = checked(a * h1 + h0);
                long k2 = checked(a * k1 + k0);
                if (k2 > maxDenominator) break;

                h0 = h1; h1 = h2; k0 = k1; k1 = k2;

                double frac = rest - whole;
                if (frac < 1e-12 || Math.Abs((double)h1 / k1 - x) < 1e-12 * Math.Max(1, x)) break;
                rest = 1 / frac;
            }

            if (k1 == 0) return new Rational(negative ? -(long)Math.Round(x) : (long)Math.Round(x), 1);
            return new Rational(negative ? -h1 : h1, k1);
        }

        /// <summary>Reads "30000/1001", "30" or "29.97"; a decimal becomes its exact fraction.</summary>
        /// <param name="text">The text.</param>
        /// <param name="value">The fraction.</param>
        /// <returns>False when the text isn't a fraction or number, or its denominator is zero.</returns>
        public static bool TryParse(string? text, out Rational value)
        {
            value = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            int slash = text.IndexOf('/');
            if (slash >= 0)
            {
                if (!long.TryParse(text.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture, out long num)) return false;
                if (!long.TryParse(text.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long den) || den == 0) return false;
                value = new Rational(num, den);
                return true;
            }

            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal d)) return false;
            value = FromDecimal(d);
            return true;
        }

        /// <summary>Reads a fraction written by <see cref="ToString"/>.</summary>
        /// <param name="text">The text.</param>
        /// <returns>The fraction.</returns>
        /// <exception cref="FormatException">The text isn't a fraction or number.</exception>
        public static Rational Parse(string text) =>
            TryParse(text, out Rational value) ? value : throw new FormatException($"'{text}' isn't a fraction.");

        /// <summary>a × b, reduced.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>The product.</returns>
        public static Rational operator *(Rational a, Rational b) => Reduce((Int128)a.Num * b.Num, (Int128)a.Den * b.Den);

        /// <summary>a ÷ b, reduced.</summary>
        /// <param name="a">The dividend.</param>
        /// <param name="b">The divisor; not zero.</param>
        /// <returns>The quotient.</returns>
        public static Rational operator /(Rational a, Rational b) => Reduce((Int128)a.Num * b.Den, (Int128)a.Den * b.Num);

        /// <summary>A whole number as a fraction.</summary>
        /// <param name="value">The value.</param>
        public static implicit operator Rational(long value) => new(value, 1);

        //a product's fraction; when it's too big for longs even reduced, the nearest one that fits
        private static Rational Reduce(Int128 num, Int128 den)
        {
            if (den == 0) throw new DivideByZeroException("A rational's denominator can't be zero.");
            if (den < 0) { num = -num; den = -den; }

            Int128 gcd = Gcd(Int128.Abs(num), den);
            if (gcd > 1) { num /= gcd; den /= gcd; }

            if (num >= long.MinValue && num <= long.MaxValue && den <= long.MaxValue) return new Rational((long)num, (long)den);
            return Approximate((double)num / (double)den, int.MaxValue);
        }

        private static Int128 Gcd(Int128 a, Int128 b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }

        /// <inheritdoc/>
        public bool Equals(Rational other) => Num == other.Num && Den == other.Den;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Rational other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => HashCode.Combine(Num, Den);

        /// <inheritdoc/>
        public int CompareTo(Rational other) => ((Int128)Num * other.Den).CompareTo((Int128)other.Num * Den);

        /// <inheritdoc/>
        public int CompareTo(object? obj) => obj is Rational other ? CompareTo(other)
            : obj is null ? 1 : throw new ArgumentException("Only a Rational compares with a Rational.", nameof(obj));

        /// <summary>Whether two fractions are equal.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when they're the same value.</returns>
        public static bool operator ==(Rational a, Rational b) => a.Equals(b);

        /// <summary>Whether two fractions differ.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when they're different values.</returns>
        public static bool operator !=(Rational a, Rational b) => !a.Equals(b);

        /// <summary>Whether a is less than b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is less.</returns>
        public static bool operator <(Rational a, Rational b) => a.CompareTo(b) < 0;

        /// <summary>Whether a is greater than b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is greater.</returns>
        public static bool operator >(Rational a, Rational b) => a.CompareTo(b) > 0;

        /// <summary>Whether a is at most b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is less or equal.</returns>
        public static bool operator <=(Rational a, Rational b) => a.CompareTo(b) <= 0;

        /// <summary>Whether a is at least b.</summary>
        /// <param name="a">The first.</param>
        /// <param name="b">The second.</param>
        /// <returns>True when a is greater or equal.</returns>
        public static bool operator >=(Rational a, Rational b) => a.CompareTo(b) >= 0;

        /// <summary>"num/den", or just "num" for a whole number.</summary>
        /// <returns>The text.</returns>
        public override string ToString() => Den == 1
            ? Num.ToString(CultureInfo.InvariantCulture)
            : $"{Num.ToString(CultureInfo.InvariantCulture)}/{Den.ToString(CultureInfo.InvariantCulture)}";
    }

    //a Rational in JSON: "num/den"
    internal sealed class RationalJsonConverter : JsonConverter<Rational>
    {
        public override Rational Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Rational.TryParse(reader.GetString(), out Rational value) ? value : throw new JsonException($"'{reader.GetString()}' is not a fraction.");

        public override void Write(Utf8JsonWriter writer, Rational value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
