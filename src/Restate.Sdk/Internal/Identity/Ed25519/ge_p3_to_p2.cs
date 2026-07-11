// Vendored from Chaos.NaCl (https://github.com/CodesInChaos/Chaos.NaCl) — verify-only subset.
// C# port by Christian Winnerlein (CodesInChaos); Ed25519 ref10 by Daniel J. Bernstein (SUPERCOP).
// Original code is public domain (CC0). See the upstream License.txt for attribution details.

namespace Restate.Sdk.Internal.Identity.Ed25519
{
    internal static partial class GroupOperations
    {
        /*
        r = p
        */
        public static void ge_p3_to_p2(out GroupElementP2 r, ref GroupElementP3 p)
        {
            r.X = p.X;
            r.Y = p.Y;
            r.Z = p.Z;
        }
    }
}