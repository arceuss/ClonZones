namespace ClonZones
{
    internal static class GuitarNoteColor
    {
        public static int Resolve(int lane, int seed, bool shuffle)
        {
            if (lane == 0 || !shuffle) return lane;
            // CH 1.1.0.6142 hashes the physical bit and note seed, but includes open in its divisor.
            // Keep the hash and give heads and ribbons the same five closed colors.
            unchecked
            {
                int hash = (lane + seed) ^ 0x5F3759DF;
                hash ^= hash >> 13;
                hash *= 0x27D4EB2D;
                hash ^= hash >> 15;
                hash += seed;
                return (hash & int.MaxValue) % 5 + 1;
            }
        }
    }
}
