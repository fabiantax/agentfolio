namespace AgentFolio.Platform.Tenancy;

/// <summary>
/// Minimal inline ULID: 48-bit millisecond timestamp + 80-bit random, Crockford
/// base32, 26 chars uppercase. No external package (spec 002 constraint).
/// </summary>
public static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string NewUlid()
    {
        Span<byte> bytes = stackalloc byte[16];
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(now >> 40);
        bytes[1] = (byte)(now >> 32);
        bytes[2] = (byte)(now >> 24);
        bytes[3] = (byte)(now >> 16);
        bytes[4] = (byte)(now >> 8);
        bytes[5] = (byte)now;
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[6..]);
        return Encode(bytes);
    }

    /// <summary>Encodes 16 bytes (big-endian ULID) as 26 Crockford base32 chars.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16) throw new ArgumentException("ULID is exactly 16 bytes.", nameof(bytes));
        Span<char> chars = stackalloc char[26];

        // Timestamp: treat the 48-bit ms value as a 50-bit string with 2 zero pad
        // bits up front; char i = 5-bit chunk [5i, 5i+4) of that string (MSB-first).
        ulong ts = ((ulong)bytes[0] << 40) | ((ulong)bytes[1] << 32)
                 | ((ulong)bytes[2] << 24) | ((ulong)bytes[3] << 16)
                 | ((ulong)bytes[4] << 8) | bytes[5];
        for (int i = 0; i < 10; i++)
        {
            int shift = 45 - 5 * i;                 // 45, 40, ..., 0
            chars[i] = Alphabet[(int)((ts >> shift) & 0x1F)];
        }

        // Randomness: bytes 6..15 form an 80-bit MSB-first string; char j (0..15)
        // = 5-bit chunk starting at absolute bit 5j of that string.
        for (int j = 0; j < 16; j++)
        {
            int bit0 = 5 * j;                       // 0,5,...,75
            int bi = 6 + (bit0 >> 3);               // byte holding that bit
            int off = bit0 & 7;                     // bit offset inside the byte (MSB=0)
            int digit;
            if (off <= 3)
            {
                // all 5 bits live in bytes[bi]
                digit = (bytes[bi] >> (3 - off)) & 0x1F;
            }
            else
            {
                // spills (off - 3) bits into bytes[bi + 1]; j <= 15 keeps bi+1 <= 15
                int spill = off - 3;
                int high = bytes[bi] & ((1 << (8 - off)) - 1);
                digit = (high << spill) | (bytes[bi + 1] >> (8 - spill));
            }
            chars[10 + j] = Alphabet[digit];
        }

        return new string(chars);
    }

    /// <summary>Crockford base32 alphabet check (26 uppercase chars).</summary>
    public static bool IsValidFormat(string value)
    {
        if (value.Length != 26) return false;
        foreach (var c in value)
            if (!Alphabet.Contains(c)) return false;
        return true;
    }
}
