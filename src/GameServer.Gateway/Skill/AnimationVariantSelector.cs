/// <summary>
/// The client's animation-choice RNG and ACT-order weighted reservoir (3.29.3b: 0x140e0bec0,
/// 0x140484fe0, 0x140484fa0, 0x141e991e0). This is deliberately the literal native algorithm:
/// its state transition shifts x right, unlike the commonly published TinyMT32 transition.
/// The same generator, seeded from a key (0x1413cbe50) and drawn in a range (0x1404d03d0), rolls each monster's
/// movement speed (MonsterSpeedRoll.cs).
/// </summary>
static class AnimationVariantSelector
{
    public static int Select(uint seed, ReadOnlySpan<uint> weights)
    {
        var random = new NativeRandom(seed);
        uint total = 0;
        int selected = -1;
        for (int i = 0; i < weights.Length; i++)
        {
            uint weight = weights[i];
            if (weight == 0) continue;
            total = checked(total + weight);
            if (random.Uniform(total) < weight) selected = i;
        }
        if (selected < 0) throw new ArgumentException("An animation pool needs a positive weight.", nameof(weights));
        return selected;
    }

    internal struct NativeRandom
    {
        private const uint Mat1 = 0x8f7011ee, Mat2 = 0xfc78ff1f, Tmat = 0x3793fdff;
        private uint _s0, _s1, _s2, _s3;

        public NativeRandom(uint seed)
        {
            Span<uint> state = stackalloc uint[] { seed, Mat1, Mat2, Tmat };
            for (uint i = 1; i < 8; i++)
                state[(int)(i & 3)] ^= unchecked(i + 0x6c078965 *
                    (state[(int)((i - 1) & 3)] ^ (state[(int)((i - 1) & 3)] >> 30)));
            (_s0, _s1, _s2, _s3) = (state[0], state[1], state[2], state[3]);
            for (int i = 0; i < 8; i++) NextState();
        }

        /// <summary>
        /// The state seeded from a key (0x1413cbe50: TinyMT32 init_by_array, the published initializer with this
        /// client's mat1/mat2/tmat, no period certification, then eight native transitions). The client's copy is
        /// specialised to the two-word key [object id, type word] of the monster speed roll.
        /// </summary>
        public NativeRandom(ReadOnlySpan<uint> key)
        {
            const int size = 4, lag = 1, mid = 1;
            Span<uint> st = stackalloc uint[] { 0, Mat1, Mat2, Tmat };
            unchecked
            {
                int count = Math.Max(key.Length + 1, 8) - 1;
                uint r = Mix1(st[0] ^ st[mid] ^ st[size - 1]);
                st[mid] += r;
                r += (uint)key.Length;
                st[(mid + lag) % size] += r;
                st[0] = r;
                int i = 1, j = 0;
                for (; j < count && j < key.Length; j++, i = (i + 1) % size)
                {
                    r = Mix1(st[i] ^ st[(i + mid) % size] ^ st[(i + size - 1) % size]);
                    st[(i + mid) % size] += r;
                    r += key[j] + (uint)i;
                    st[(i + mid + lag) % size] += r;
                    st[i] = r;
                }
                for (; j < count; j++, i = (i + 1) % size)
                {
                    r = Mix1(st[i] ^ st[(i + mid) % size] ^ st[(i + size - 1) % size]);
                    st[(i + mid) % size] += r;
                    r += (uint)i;
                    st[(i + mid + lag) % size] += r;
                    st[i] = r;
                }
                for (j = 0; j < size; j++, i = (i + 1) % size)
                {
                    r = Mix2(st[i] + st[(i + mid) % size] + st[(i + size - 1) % size]);
                    st[(i + mid) % size] ^= r;
                    r -= (uint)i;
                    st[(i + mid + lag) % size] ^= r;
                    st[i] = r;
                }
            }
            (_s0, _s1, _s2, _s3) = (st[0], st[1], st[2], st[3]);
            for (int n = 0; n < 8; n++) NextState();
        }

        private static uint Mix1(uint x) => unchecked((x ^ (x >> 27)) * 0x19660d);
        private static uint Mix2(uint x) => unchecked((x ^ (x >> 27)) * 0x5d588b65);

        private void NextState()
        {
            uint x = (_s0 & 0x7fffffff) ^ _s1 ^ _s2;
            uint y = _s3 ^ (_s3 << 1);
            uint z = x ^ (x >> 1) ^ y;
            bool odd = (z & 1) != 0;
            (_s0, _s1, _s2, _s3) = (_s1, _s2 ^ (odd ? Mat1 : 0),
                y ^ (z << 10) ^ (odd ? Mat2 : 0), z);
        }

        public uint NextUInt()
        {
            NextState();
            uint t1 = unchecked(_s0 + (_s2 >> 8));
            return _s3 ^ t1 ^ ((t1 & 1) != 0 ? Tmat : 0);
        }

        public uint Uniform(uint bound)
        {
            if (bound == 0) throw new ArgumentOutOfRangeException(nameof(bound));
            // A one-entry pool must not advance the RNG: the later reservoir choices depend on this.
            if (bound == 1) return 0;
            uint quotient = uint.MaxValue / bound, remainder = uint.MaxValue % bound;
            while (true)
            {
                uint value = NextUInt();
                if (value / bound < quotient || remainder == bound - 1) return value % bound;
            }
        }

        /// <summary>
        /// A uniform integer in [min, max] (0x1404d03d0): the bounded draw over max − min + 1 values plus min, or a
        /// raw word when the range covers every value.
        /// </summary>
        public int Uniform(int min, int max)
        {
            uint span = unchecked((uint)(max - min));
            return unchecked((int)((span == uint.MaxValue ? NextUInt() : Uniform(span + 1)) + (uint)min));
        }
    }
}
