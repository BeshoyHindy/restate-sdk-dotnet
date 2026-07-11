// Vendored from Chaos.NaCl (https://github.com/CodesInChaos/Chaos.NaCl) — verify-only subset.
// C# port by Christian Winnerlein (CodesInChaos); Ed25519 ref10 by Daniel J. Bernstein (SUPERCOP).
// Original code is public domain (CC0). See the upstream License.txt for attribution details.

namespace Restate.Sdk.Internal.Identity.Ed25519
{
    internal static partial class GroupOperations
    {
        public static void ge_tobytes(Span<byte> s, int offset, ref GroupElementP2 h)
        {
            FieldElement recip;
            FieldElement x;
            FieldElement y;

            FieldOperations.fe_invert(out recip, ref h.Z);
            FieldOperations.fe_mul(out x, ref h.X, ref recip);
            FieldOperations.fe_mul(out y, ref h.Y, ref recip);
            FieldOperations.fe_tobytes(s, offset, ref y);
            s[offset + 31] ^= (byte)(FieldOperations.fe_isnegative(ref x) << 7);
        }
    }
}