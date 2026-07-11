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
        public static void ge_p1p1_to_p3(out GroupElementP3 r, ref GroupElementP1P1 p)
        {
            FieldOperations.fe_mul(out r.X, ref p.X, ref p.T);
            FieldOperations.fe_mul(out r.Y, ref p.Y, ref p.Z);
            FieldOperations.fe_mul(out r.Z, ref p.Z, ref p.T);
            FieldOperations.fe_mul(out r.T, ref p.X, ref p.Y);
        }
    }
}