namespace Game.Core.Security;

/// <summary>
/// RFC 4648 base32, the alphabet authenticator apps use for their secret keys. Identity creates
/// keys in it, but its own decoder isn't public.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The bytes of <paramref name="text"/>; spaces, dashes, padding and case are ignored.</summary>
    /// <exception cref="FormatException">A character outside the alphabet.</exception>
    public static byte[] Decode(string text)
    {
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-' or '=') continue;
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (value < 0) throw new FormatException($"'{c}' isn't a base32 character.");

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)(buffer >> bits));
                buffer &= (1 << bits) - 1;
            }
        }
        return bytes.ToArray();
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text.Append(Alphabet[(buffer >> bits) & 31]);
            }
            buffer &= (1 << bits) - 1;
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }
}
