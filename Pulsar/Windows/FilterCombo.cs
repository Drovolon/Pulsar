using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Pulsar.Windows;

/// <summary>
/// A single-line combo that expands into a search box + clipped, filtered list.
/// Similar to Luna/Penumbra/Glamourer.
/// </summary>
internal sealed class FilterCombo<T>(Func<T, string> getLabel, float listHeight = 320f)
    where T : class
{
    private string filter = "";
    private bool wasOpen;
    private IReadOnlyList<T>? lastItems;
    private string lastFilter = "";
    private List<T> filtered = [];

    /// <summary>Returns true only on the frame an item is chosen; the pick lands in <paramref name="result"/>.</summary>
    public bool Draw(string id, string preview, IReadOnlyList<T> items, T? current, out T? result)
    {
        result = null;
        var changed = false;

        using var combo = ImRaii.Combo(id, preview, ImGuiComboFlags.HeightLargest);
        if (!combo)
        {
            wasOpen = false;
            return false;
        }

        // Focus the search box the first frame the popup opens, starting from a fresh filter.
        if (!wasOpen)
        {
            wasOpen = true;
            filter = "";
            ImGui.SetKeyboardFocusHere();
        }

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##filter", "Search...", ref filter, 256);

        // Re-filter only when the query or the source list changed.
        if (!ReferenceEquals(items, lastItems) || filter != lastFilter)
        {
            lastItems = items;
            lastFilter = filter;
            filtered = filter.Length == 0
                ? [.. items]
                : [.. items.Where(i => getLabel(i).Contains(filter, StringComparison.OrdinalIgnoreCase))];
        }

        using var child = ImRaii.Child("##list", new Vector2(0f, listHeight), true);
        if (child)
        {
            var cmp = EqualityComparer<T>.Default;
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(filtered.Count);
            while (clipper.Step())
                for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    var item = filtered[i];
                    if (ImGui.Selectable($"{getLabel(item)}##fc{i}", cmp.Equals(item, current)))
                    {
                        result = item;
                        changed = true;
                        ImGui.CloseCurrentPopup();
                    }
                }
            clipper.End();
            clipper.Destroy();
        }

        return changed;
    }
}
