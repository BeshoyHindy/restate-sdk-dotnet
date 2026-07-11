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
        public static void ge_p3_to_cached(out GroupElementCached r, ref GroupElementP3 p)
        {
            FieldOperations.fe_add(out r.YplusX, ref p.Y, ref p.X);
            FieldOperations.fe_sub(out r.YminusX, ref p.Y, ref p.X);
            r.Z = p.Z;
            FieldOperations.fe_mul(out r.T2d, ref p.T, ref LookupTables.d2);
        }
    }
}