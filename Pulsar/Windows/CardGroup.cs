using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Pulsar.Windows;

/// <summary>
/// One option in a CardGroup. Accent is used if it's the current selection. Trailing is a right-aligned
/// label. Note is a short tag drown after the label.
/// </summary>
internal readonly record struct CardOption(
    string Label,
    string Description,
    Vector4 Accent,
    string? Trailing = null,
    string? Tooltip = null,
    string? Note = null);

/// <summary>
/// Stolen from Lightless, a vertical stack of radio card buttons. I say "stolen" - the author of this
/// code originally wrote it in Lightless. So, well. I stole my own thing. It's all AGPL, right?
/// </summary>
internal static class CardGroup
{
    /// <summary>Draws the stack. Returns the newly-clicked index, or -1 if nothing changed this frame.</summary>
    public static int Draw(string id, ReadOnlySpan<CardOption> options, int selectedIndex, float width)
    {
        const float padX = 14f, padY = 10f, gap = 6f, rounding = 8f, dotRadius = 6f, dotGap = 10f, labelDescGap = 2f;

        var scale = ImGuiHelpers.GlobalScale;
        var alpha = ImGui.GetStyle().Alpha;
        var drawList = ImGui.GetWindowDrawList();
        var startX = ImGui.GetCursorPosX();
        var result = -1;

        var win = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg];
        var baseBg = new Vector4(win.X + 0.04f, win.Y + 0.04f, win.Z + 0.04f, 0.97f);

        using var idScope = ImRaii.PushId(id);

        for (var i = 0; i < options.Length; i++)
        {
            if (i > 0) ImGui.Dummy(new Vector2(0f, gap * scale));
            ImGui.SetCursorPosX(startX);

            var opt = options[i];
            var selected = i == selectedIndex;

            // I love ImGui. It's the best.
            var pX = padX * scale;
            var pY = padY * scale;
            var dotR = dotRadius * scale;
            var textStartX = (dotR * 2f) + (dotGap * scale);
            var contentW = MathF.Max(1f, width - (pX * 2f) - textStartX);
            var lineH = ImGui.GetTextLineHeight();

            var trailing = opt.Trailing ?? string.Empty;
            var trailingW = trailing.Length > 0 ? ImGui.CalcTextSize(trailing).X : 0f;
            var note = opt.Note ?? string.Empty;
            var noteW = note.Length > 0 ? ImGui.CalcTextSize(note).X + (gap * scale) : 0f;
            var labelMaxW = MathF.Max(1f, contentW - (trailingW > 0f ? trailingW + (gap * scale) : 0f));
            var label = UiUtil.Ellipsize(opt.Label, MathF.Max(1f, labelMaxW - noteW));
            var desc = UiUtil.Ellipsize(opt.Description, contentW);

            var cardH = (pY * 2f) + lineH + (labelDescGap * scale) + lineH;

            if (ImGui.InvisibleButton($"##card{i}", new Vector2(width, cardH)) && !selected)
                result = i;

            var hovered = ImGui.IsItemHovered();
            if (hovered && opt.Tooltip is { Length: > 0 } tip)
                ImGui.SetTooltip(tip);

            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var afterCursor = ImGui.GetCursorPos();
            var textAlpha = selected ? 1f : 0.7f;
            var round = rounding * scale;

            drawList.AddRectFilled(min, max, ImGui.GetColorU32(A(baseBg, alpha)), round);
            if (selected)
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(A(opt.Accent with { W = 0.10f }, alpha)), round);
            if (hovered)
                drawList.AddRectFilled(min, max, ImGui.GetColorU32(A(new Vector4(1f, 1f, 1f, 0.05f), alpha)), round);

            var border = selected
                ? opt.Accent with { W = hovered ? 0.90f : 0.65f }
                : new Vector4(0.5f, 0.5f, 0.5f, hovered ? 0.35f : 0.15f);
            drawList.AddRect(min, max, ImGui.GetColorU32(A(border, alpha)), round, ImDrawFlags.None, 1f * scale);

            var dotCenter = new Vector2(min.X + pX + dotR, min.Y + pY + (lineH * 0.5f));
            if (selected)
                drawList.AddCircleFilled(dotCenter, dotR, ImGui.GetColorU32(A(opt.Accent with { W = 0.9f }, alpha)));
            else
                drawList.AddCircle(dotCenter, dotR, ImGui.GetColorU32(A(new Vector4(0.5f, 0.5f, 0.5f, 0.6f * textAlpha), alpha)), 0, 1.5f * scale);

            var labelPos = new Vector2(min.X + pX + textStartX, min.Y + pY);
            drawList.AddText(labelPos, ImGui.GetColorU32(A(ImGuiColors.DalamudWhite with { W = 0.95f * textAlpha }, alpha)), label);
            if (note.Length > 0)
            {
                var notePos = new Vector2(labelPos.X + ImGui.CalcTextSize(label).X + (gap * scale), labelPos.Y);
                drawList.AddText(notePos, ImGui.GetColorU32(A(opt.Accent with { W = 0.95f }, alpha)), note);
            }
            if (trailingW > 0f)
            {
                var trailPos = new Vector2(max.X - pX - trailingW, labelPos.Y);
                drawList.AddText(trailPos, ImGui.GetColorU32(A(new Vector4(0.7f, 0.7f, 0.7f, 0.9f * textAlpha), alpha)), trailing);
            }

            var descPos = new Vector2(labelPos.X, labelPos.Y + lineH + (labelDescGap * scale));
            drawList.AddText(descPos, ImGui.GetColorU32(A(new Vector4(0.7f, 0.7f, 0.7f, 0.9f * textAlpha), alpha)), desc);

            ImGui.SetCursorPos(afterCursor);
        }

        return result;
    }

    private static Vector4 A(Vector4 color, float alpha) => color with { W = color.W * alpha };
}
