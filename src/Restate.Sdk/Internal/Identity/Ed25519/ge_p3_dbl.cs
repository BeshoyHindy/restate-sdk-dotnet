// Vendored from Chaos.NaCl (https://github.com/CodesInChaos/Chaos.NaCl) — verify-only subset.
// C# port by Christian Winnerlein (CodesInChaos); Ed25519 ref10 by Daniel J. Bernstein (SUPERCOP).
// Original code is public domain (CC0). See the upstream License.txt for attribution details.

namespace Restate.Sdk.Internal.Identity.Ed25519
{
    internal static partial class GroupOperations
    {
        /*
        r = 2 * p
        */
        public static void ge_p3_dbl(out GroupElementP1P1 r, ref GroupElementP3 p)
        {
            GroupElementP2 q;
            ge_p3_to_p2(out q, ref p);
            ge_p2_dbl(out r, ref q);
        }
    }
}