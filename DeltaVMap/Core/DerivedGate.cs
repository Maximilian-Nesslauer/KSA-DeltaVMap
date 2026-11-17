using System.Reflection;
using HarmonyLib;
using KSA;

namespace DeltaVMap.Core;

// A flight read that would run PartTree.EnsureDerived waits while the tree has pending derived
// work, because the vehicle solver batch runs on the same tree during the draw (the frame order is
// in the header of StagedDv). Every such read asks this one gate. The flag word is the private
// PartTree._derivedDirty, so re-check it on each game update.
internal static class DerivedGate
{
    private static readonly AccessTools.FieldRef<PartTree, DerivedData>? DerivedDirty = Bind();

    // True when tree has pending derived work in any of inputs. Without the key it warns once and
    // returns false, so the reads go on without the wait instead of stopping.
    internal static bool HasPending(PartTree tree, DerivedData inputs)
    {
        if (DerivedDirty == null)
        {
            LogHelper.WarnOnce("derived-dirty",
                "[DvMap] PartTree._derivedDirty not found, flight reads no longer wait for the derived-data flush.");
            return false;
        }
        return (DerivedDirty(tree) & inputs) != DerivedData.None;
    }

    private static AccessTools.FieldRef<PartTree, DerivedData>? Bind()
    {
        FieldInfo? field = AccessTools.DeclaredField(typeof(PartTree), "_derivedDirty");
        return field != null && field.FieldType == typeof(DerivedData) && !field.IsStatic
            ? AccessTools.FieldRefAccess<PartTree, DerivedData>(field)
            : null;
    }
}
