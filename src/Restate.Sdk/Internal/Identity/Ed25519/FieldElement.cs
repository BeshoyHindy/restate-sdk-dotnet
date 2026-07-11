// Vendored from Chaos.NaCl (https://github.com/CodesInChaos/Chaos.NaCl) — verify-only subset.
// C# port by Christian Winnerlein (CodesInChaos); Ed25519 ref10 by Daniel J. Bernstein (SUPERCOP).
// Original code is public domain (CC0). See the upstream License.txt for attribution details.

namespace Restate.Sdk.Internal.Identity.Ed25519
{
    internal struct FieldElement
    {
        internal int x0;
        internal int x1;
        internal int x2;
        internal int x3;
        internal int x4;
        internal int x5;
        internal int x6;
        internal int x7;
        internal int x8;
        internal int x9;

        //public static readonly FieldElement Zero = new FieldElement();
        //public static readonly FieldElement One = new FieldElement() { x0 = 1 };

        internal FieldElement(params int[] elements)
        {
            System.Diagnostics.Debug.Assert(elements.Length == 10, "elements.Length != 10");
            x0 = elements[0];
            x1 = elements[1];
            x2 = elements[2];
            x3 = elements[3];
            x4 = elements[4];
            x5 = elements[5];
            x6 = elements[6];
            x7 = elements[7];
            x8 = elements[8];
            x9 = elements[9];
        }
    }
}
