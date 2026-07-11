// Vendored from Chaos.NaCl (https://github.com/CodesInChaos/Chaos.NaCl) — verify-only subset.
// C# port by Christian Winnerlein (CodesInChaos); Ed25519 ref10 by Daniel J. Bernstein (SUPERCOP).
// Original code is public domain (CC0). See the upstream License.txt for attribution details.

namespace Restate.Sdk.Internal.Identity.Ed25519
{
    internal static partial class GroupOperations
    {
        public static void ge_p2_0(out GroupElementP2 h)
        {
            FieldOperations.fe_0(out h.X);
            FieldOperations.fe_1(out h.Y);
            FieldOperations.fe_1(out h.Z);
        }
    }
}