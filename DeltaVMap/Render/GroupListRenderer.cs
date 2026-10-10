using System;
using System.Collections.Generic;
using System.Globalization;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using DeltaVMap.Core;
using DeltaVMap.Dv;
using DeltaVMap.Model;
using KSA;

namespace DeltaVMap.Render;

// The member list of a "+N" group, drawn into a canvas overlay that the window places. It lists
// every folded body, innermost first, with its mean radius, its orbit radius around the hub and a
// mark for a retrograde orbit, and a click on a row picks that body. A group of folded star
// systems lists only the names. The rows are built once per group node, so the rebuild that a
// pick causes (a new node) refreshes them; the filter narrows them, and at most MaxRows draw.
internal sealed class GroupListRenderer
{
    private const int MaxRows = 200;
    private const int FilterCapacity = 64;

    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;

    private readonly ImInputString _filter = new ImInputString(FilterCapacity);
    private string _query = "";
    private string? _groupId;
    private StateNode? _group;
    private string _title = "";
    private bool _systems;
    private readonly List<Row> _rows = new();
    private readonly List<string> _names = new();
    private readonly List<int> _shown = new();
    private int _matches;
    private string _moreText = "";

    private readonly record struct Row(PhysicalNode Body, string Radius, string Orbit, bool Retrograde);

    public void Reset()
    {
        _filter.Clear();
        _query = "";
        _groupId = null;
        _group = null;
        _rows.Clear();
        _names.Clear();
        _shown.Clear();
    }

    // Builds the rows for the group when it is a new node and returns how many rows the filter
    // leaves, so the caller can size the panel before Draw. Another group starts unfiltered.
    public int Prepare(StateNode group)
    {
        if (group.Id != _groupId)
        {
            _groupId = group.Id;
            _filter.Clear();
            _query = "";
            _group = null;
        }
        if (!ReferenceEquals(group, _group))
        {
            BuildRows(group);
            Refilter();
            _group = group;
        }
        return _shown.Count;
    }

    // Draws the list into the current child window. Returns the picked body, if any, and sets
    // close when the player closes the list.
    public PhysicalNode? Draw(out bool close)
    {
        PhysicalNode? pick = null;
        ImGui.Text(_title);
        ImGui.SameLine();
        close = ImGui.SmallButton("Close##dvmgrouplist"u8);

        ImGui.PushItemWidth(-1f);
        if (ImGui.InputTextWithHint("##dvmgroupfilter"u8, "Filter bodies..."u8, _filter))
        {
            string query = _filter.ToString();
            if (query != _query)
            {
                _query = query;
                Refilter();
            }
        }
        ImGui.PopItemWidth();

        ImGui.PushTextWrapPos(0f);
        ImGui.TextDisabled(_systems ? "Click a system to open it." : "Click a body to show it on the map and plan a route to it.");
        ImGui.PopTextWrapPos();

        if (_shown.Count == 0)
        {
            ImGui.TextDisabled("No bodies match.");
            return null;
        }

        bool more = _matches > _shown.Count;
        float moreH = more ? ImGui.GetTextLineHeightWithSpacing() : 0f;
        var outer = new float2(0f, Math.Max(ImGui.GetContentRegionAvail().Y - moreH, ImGui.GetFrameHeight() * 2f));
        if (ImGui.BeginTable("##dvmgrouptable"u8, _systems ? 1 : 4, TableFlags, (float2?)outer))
        {
            try
            {
                ImGui.TableSetupColumn("Body"u8, ImGuiTableColumnFlags.WidthStretch, 1.6f, 0);
                if (!_systems)
                {
                    ImGui.TableSetupColumn("Radius"u8, ImGuiTableColumnFlags.WidthStretch, 0.8f, 1);
                    ImGui.TableSetupColumn("Orbit"u8, ImGuiTableColumnFlags.WidthStretch, 1.1f, 2);
                    ImGui.TableSetupColumn("Retro"u8, ImGuiTableColumnFlags.WidthStretch, 0.5f, 3);
                }
                // Keep the header visible while the body scrolls.
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableHeadersRow();

                foreach (int i in _shown)
                {
                    Row row = _rows[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable(_names[i], false, ImGuiSelectableFlags.SpanAllColumns, (float2?)null))
                        pick = row.Body;
                    if (_systems)
                        continue;
                    ImGui.TableNextColumn();
                    ImGui.Text(row.Radius);
                    ImGui.TableNextColumn();
                    ImGui.Text(row.Orbit);
                    ImGui.TableNextColumn();
                    if (row.Retrograde)
                        ImGui.Text("yes"u8);
                }
            }
            finally
            {
                // Always close the table, even on an unexpected throw, so the table stack stays
                // balanced for the rest of the overlay.
                ImGui.EndTable();
            }
        }

        if (more)
            ImGui.TextDisabled(_moreText);
        return pick;
    }

    private void BuildRows(StateNode group)
    {
        _rows.Clear();
        _names.Clear();
        IReadOnlyList<PhysicalNode> members = group.GroupMembers ?? Array.Empty<PhysicalNode>();
        _systems = members.Count > 0 && members[0].IsSystemRoot;
        _title = _systems ? group.Label : group.Label + " of " + group.Body.Id;

        foreach (PhysicalNode member in members)
        {
            _names.Add(member.Id);
            if (_systems)
            {
                _rows.Add(new Row(member, "", "", false));
                continue;
            }
            Orbit orbit = ((IOrbiter)member.Astro).Orbit;
            // The orbit is held in the hub's own inertial frame (OrbitTemplate.CreateEcliptic
            // converts an ecliptic definition into the parent's CCI), so its inclination above
            // 90 degrees is a retrograde orbit around the hub. Inclination is in radians.
            bool retrograde = orbit.Inclination > Math.PI / 2.0;
            _rows.Add(new Row(member, RadiusText(member.Ladder.MeanRadius), Format.Distance(OrbitalStates.TransferRadius(orbit)), retrograde));
        }
    }

    private void Refilter()
    {
        _matches = GroupListFilter.Apply(_names, _query, MaxRows, _shown);
        int hidden = _matches - _shown.Count;
        _moreText = hidden > 0 ? "+" + hidden.ToString(CultureInfo.InvariantCulture) + " more, refine the filter" : "";
    }

    // Many long-tail moons are a few kilometers across, so small radii keep one decimal.
    private static string RadiusText(double meters)
    {
        return meters < 10000.0
            ? (meters / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " km"
            : Format.Km(meters);
    }
}
