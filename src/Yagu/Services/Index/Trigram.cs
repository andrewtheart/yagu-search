namespace Yagu.Services.Index;

/// <summary>
/// A single trigram — three consecutive bytes of the canonical UTF-8/LF content representation
/// (plan §3.1/§3.2). Packed into the low 24 bits of a <see cref="uint"/> so trigram sets sort and
/// compare cheaply. This is the atomic unit of the posting-list index and of the boolean trigram
/// queries produced by <see cref="TrigramQueryPlanner"/>.
/// </summary>
public readonly struct Trigram : IEquatable<Trigram>, IComparable<Trigram>
{
    /// <summary>The three bytes packed as <c>0x00_bbbbbb</c> (byte0 high, byte2 low).</summary>
    public uint Value { get; }

    public Trigram(byte byte0, byte byte1, byte byte2)
        => Value = ((uint)byte0 << 16) | ((uint)byte1 << 8) | byte2;

    /// <summary>Wraps an already-packed 24-bit value. Bits above the low 24 are ignored.</summary>
    public static Trigram FromPacked(uint packed) => new(packed);

    private Trigram(uint packed) => Value = packed & 0xFF_FFFF;

    public byte Byte0 => (byte)(Value >> 16);
    public byte Byte1 => (byte)(Value >> 8);
    public byte Byte2 => (byte)Value;

    public bool Equals(Trigram other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is Trigram other && Equals(other);

    public override int GetHashCode() => (int)Value;

    public int CompareTo(Trigram other) => Value.CompareTo(other.Value);

    public static bool operator ==(Trigram left, Trigram right) => left.Equals(right);

    public static bool operator !=(Trigram left, Trigram right) => !left.Equals(right);

    public static bool operator <(Trigram left, Trigram right) => left.CompareTo(right) < 0;

    public static bool operator >(Trigram left, Trigram right) => left.CompareTo(right) > 0;

    public static bool operator <=(Trigram left, Trigram right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Trigram left, Trigram right) => left.CompareTo(right) >= 0;

    /// <summary>Hex form (e.g. <c>66 6F 6F</c> for "foo"), for diagnostics/tests.</summary>
    public override string ToString() => $"{Byte0:X2} {Byte1:X2} {Byte2:X2}";
}
