// MeitouMoonscript: the script's random numbers. Author: PZA. Licence: GPLv3 or later.
//
// Exact definitions (claim C6 and the Rng section of tools/euler/CLAIMS.md):
//   state: one ulong; Next() is SplitMix64 (Steele, Lea, Flood 2014): state += 0x9E3779B97F4A7C15, then the two xor-shift-multiply rounds.
//   math.random()      = (Next() >> 11) * 2^-53, so a float in [0, 1) on the 2^53 grid.
//   math.random(m)     = math.random(1, m).
//   math.random(m, n)  : a = floor(m), b = floor(n); error unless a <= b and b - a < 2^53; r = b - a + 1;
//                        draw x = Next() >> 11 (53 bits) until x < L, L = 2^53 - (2^53 mod r); result a + (x mod r).
//                        Every value of [a, b] then comes from exactly L / r of the 2^53 draws: exactly uniform.
//   math.randomseed(x) : state = Mix(bits of x as a double). Deterministic, saved with the script.
// The seed comes from the host (the world's seed, the mod id and the script name); never from the clock.
namespace PZA.Moonscript.Core;

public sealed class ScriptRng(ulong seed)
{
    public ulong State = seed;

    public ulong Next()
    {
        ulong z = State += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public const double TwoTo53 = 9007199254740992.0;

    public double NextUnit() => (Next() >> 11) * (1.0 / TwoTo53);

    /// <summary>Uniform integer in [a, b]; the caller checked a &lt;= b and b - a &lt; 2^53.</summary>
    public double NextRange(double a, double b) => a + Pick((ulong)(b - a) + 1, Next);

    /// <summary>The rejection step on its own, over any 53-bit draw source (the Euler pass runs it on a counting source).</summary>
    public static ulong Pick(ulong r, Func<ulong> next) => PickBits(r, 53, () => next() >> 11);

    /// <summary>The rejection rule for a draw of `bits` bits (1..63): accept x &lt; L = 2^bits - (2^bits mod r), answer x mod r.
    /// Claim: over all 2^bits draws, every answer in [0, r) comes from exactly L / r of them (tools/euler, rng.exhaustive, bits = 12).</summary>
    public static ulong PickBits(ulong r, int bits, Func<ulong> nextBits)
    {
        ulong span = 1UL << bits;
        ulong limit = span - span % r;
        while (true)
        {
            ulong x = nextBits();
            if (x < limit) return x % r;
        }
    }

    public static ulong AcceptLimit(ulong r, int bits) { ulong span = 1UL << bits; return span - span % r; }

    public static ulong Mix(ulong x)
    {
        var r = new ScriptRng(x);
        return r.Next();
    }

    /// <summary>FNV-1a 64 of a string, for seeds made of names.</summary>
    public static ulong Hash(string s, ulong h = 14695981039346656037UL)
    {
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }
}
