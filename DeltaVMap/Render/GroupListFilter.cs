using System;
using System.Collections.Generic;

namespace DeltaVMap.Render;

// The row filter of the group member list, free of game types so it is unit-tested offline.
internal static class GroupListFilter
{
    // Fills shown with the indices of the names that contain the query (case-insensitive, blank
    // matches all), in their order and at most cap of them, and returns how many matched in total.
    public static int Apply(IReadOnlyList<string> names, string query, int cap, List<int> shown)
    {
        shown.Clear();
        string q = query.Trim();
        int matches = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (q.Length > 0 && names[i].IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            matches++;
            if (shown.Count < cap)
                shown.Add(i);
        }
        return matches;
    }
}
