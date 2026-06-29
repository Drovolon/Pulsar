using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Pulsar.Windows;

internal readonly record struct FaderResult(bool Changed, bool Committed, bool MuteToggled);

internal static class UiUtil
{
    internal static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    internal static void IconColored(FontAwesomeIcon icon, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
    }

    internal static void NowPlaying(UiTheme theme, FontAwesomeIcon icon, string text)
    {
        using (theme.MediumFont.Push())
        {
            var iconStr = icon.ToIconString();
            var iconW = ImGui.CalcTextSize(iconStr).X;
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var avail = ImGui.GetContentRegionAvail().X;

            var y0 = ImGui.GetCursorPosY();
            var nudge = 6;
            ImGui.SetCursorPosY(y0 + nudge);
            ImGui.TextUnformatted(iconStr);
            ImGui.SameLine();
            ImGui.SetCursorPosY(y0);
            ImGui.TextUnformatted(Ellipsize(text, avail - iconW - spacing));
        }
    }

    // Thin, full-width progress bar, used for showing how far we are into a track.
    // Has an optional time readout on the right.
    internal static void ProgressMeter(UiTheme theme, float fraction, bool playing, string? trailing = null)
    {
        var (origin, barW, rowH) = MeterLayout(trailing);
        DrawMeterBar(theme, origin, barW, rowH, fraction, playing, knob: false);
        ImGui.Dummy(new Vector2(barW, rowH));
        MeterTrailing(trailing);
    }

    internal readonly record struct ScrubResult(bool Activated, bool Deactivated);

    // An interactive ProgressMeter. Has a draggable knob.
    internal static ScrubResult Scrubber(UiTheme theme, string id, ref float value, float max, bool playing,
        string? trailing = null)
    {
        var (origin, barW, rowH) = MeterLayout(trailing);

        ImGui.InvisibleButton(id, new Vector2(barW, rowH));
        var activated = ImGui.IsItemActivated();
        var deactivated = ImGui.IsItemDeactivated();
        if (ImGui.IsItemActive() && max > 0f)
            value = Math.Clamp((ImGui.GetMousePos().X - origin.X) / barW, 0f, 1f) * max;
        if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        DrawMeterBar(theme, origin, barW, rowH, max > 0f ? value / max : 0f, playing, knob: true);
        MeterTrailing(trailing);
        return new ScrubResult(activated, deactivated);
    }

    private static (Vector2 Origin, float BarW, float RowH) MeterLayout(string? trailing)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var trailingW = string.IsNullOrEmpty(trailing) ? 0f : ImGui.CalcTextSize(trailing).X + spacing;
        var barW = MathF.Max(1f, avail - trailingW);
        return (ImGui.GetCursorScreenPos(), barW, ImGui.GetTextLineHeight());
    }

    private static void MeterTrailing(string? trailing)
    {
        if (string.IsNullOrEmpty(trailing)) return;
        ImGui.SameLine();
        ImGui.TextUnformatted(trailing);
    }

    // Meter bar used for progress
    private static void DrawMeterBar(UiTheme theme, Vector2 origin, float barW, float rowH, float fraction,
        bool playing, bool knob)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var h = 3f * scale;
        var rounding = h * 0.5f;
        var top = origin.Y + ((rowH - h) * 0.5f) + (2f * scale); // nudge by 2px for better alignment
        var frac = Math.Clamp(fraction, 0f, 1f);

        var dl = ImGui.GetWindowDrawList();
        var fillCol = playing ? theme.Accent : theme.Accent with { W = 0.5f };
        dl.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + barW, top + h),
            ImGui.GetColorU32(theme.Neutral with { W = 0.25f }), rounding);
        if (frac > 0f)
            dl.AddRectFilled(new Vector2(origin.X, top), new Vector2(origin.X + (barW * frac), top + h),
                ImGui.GetColorU32(fillCol), rounding);
        if (knob)
        {
            var c = new Vector2(origin.X + (barW * frac), top + (h * 0.5f));
            var r = 5f * scale;
            dl.AddCircleFilled(c, r, ImGui.GetColorU32(fillCol));
            dl.AddCircle(c, r, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.85f)), 0, 1.5f * scale);
        }
    }

    // Icon button with a hover tooltip
    internal static bool IconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        var pressed = ImGuiComponents.IconButton(id, icon);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return pressed;
    }

    internal static string Ellipsize(string text, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
            return text;

        const string ellipsis = "...";
        var ellipsisW = ImGui.CalcTextSize(ellipsis).X;
        var end = text.Length;
        while (end > 0 && ImGui.CalcTextSize(text[..end]).X + ellipsisW > maxWidth)
            end--;
        return end <= 0 ? ellipsis : text[..end] + ellipsis;
    }

    // Centered header flanked by HRs (e.g. "---- SOURCE ----").
    internal static void SectionHeader(UiTheme theme, string text)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGuiHelpers.ScaledDummy(3f);

        Vector2 size;
        using (theme.HeaderFont.Push())
            size = ImGui.CalcTextSize(text);

        var origin = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var midY = origin.Y + (size.Y * 0.5f);
        var centerX = origin.X + (avail * 0.5f);
        var half = (size.X * 0.5f) + (10f * scale);
        var col = ImGui.GetColorU32(theme.Neutral with { W = 0.35f });
        var dl = ImGui.GetWindowDrawList();
        dl.AddLine(new Vector2(origin.X, midY), new Vector2(centerX - half, midY), col, 1f * scale);
        dl.AddLine(new Vector2(centerX + half, midY), new Vector2(origin.X + avail, midY), col, 1f * scale);

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ((avail - size.X) * 0.5f));
        using (theme.HeaderFont.Push())
            ImGui.TextUnformatted(text);

        ImGuiHelpers.ScaledDummy(2f);
    }

    // Use a cubic taper (same as PulseAudio on Linux, fwiw) for the volume slider.
    internal static float SliderToAmplitude(float pos) => pos * pos * pos;
    // We use linear amplitude in the app itself; the cubic taper is only UI-side.
    internal static float AmplitudeToSlider(float amp) => MathF.Cbrt(amp);

    internal static FaderResult Fader(UiTheme theme, string id, string label, ref float value,
        bool muted, bool enabled = true, float? labelWidth = null, string? sliderTooltip = null)
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var idScope = ImRaii.PushId(id);
        using var disabled = ImRaii.Disabled(!enabled);

        var fp = ImGui.GetStyle().FramePadding;
        using var pad = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(fp.X + (3f * scale), fp.Y + (2f * scale)));

        var labelW = labelWidth ?? (96f * scale);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Ellipsize(label, labelW - (6f * scale)));

        ImGui.SameLine(labelW);

        var muteWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var sliderW = MathF.Max(60f * scale, avail - muteWidth);

        ImGui.SetNextItemWidth(sliderW);
        var pos = AmplitudeToSlider(value);
        var changed = ImGui.SliderFloat("##v", ref pos, 0f, 1f, $"{pos * 100f:0}%%");
        if (changed) value = SliderToAmplitude(pos);
        if (sliderTooltip is not null && ImGui.IsItemHovered()) ImGui.SetTooltip(sliderTooltip);
        var committed = ImGui.IsItemDeactivatedAfterEdit();

        ImGui.SameLine();
        var muteToggled = IconButton("mute", muted ? FontAwesomeIcon.VolumeMute : FontAwesomeIcon.VolumeUp,
            muted ? "Unmute" : "Mute");

        return new FaderResult(changed, committed, muteToggled);
    }
}
