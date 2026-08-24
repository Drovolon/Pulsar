using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin;

namespace Pulsar.Windows;

/// <summary>
/// The shared visual language for Pulsar's tabs: fonts plus a couple of accent colors. Owns the font
/// handles it creates (so it disposes them, unlike the built-in <c>UiBuilder.IconFont</c>). Constructed
/// by <see cref="MainWindow"/> and handed to each tab.
/// </summary>
internal sealed class UiTheme : IDisposable
{
    /// <summary>The current selection (Auto-play / a pinned source) and the "(autoplaying)" tag. Hunter green.</summary>
    public Vector4 Accent { get; } = new(0.30f, 0.62f, 0.37f, 1f);

    /// <summary>Blue used for manual queue markers.</summary>
    public Vector4 QueueAccent { get; } = new(0.30f, 0.443333f, 0.62f, 1f);

    /// <summary>"Off", a muted red.</summary>
    public Vector4 Silent { get; } = new(0.72f, 0.33f, 0.33f, 1f);

    /// <summary>Neutral tone for unselected cards and the section rules.</summary>
    public Vector4 Neutral { get; } = new(0.55f, 0.58f, 0.66f, 1f);

    /// <summary>Axis font at 18pt.</summary>
    public IFontHandle HeaderFont { get; }

    /// <summary>1.2x default font with FontAwesome merged in.</summary>
    public IFontHandle MediumFont { get; }

    /// <summary>Default font with FontAwesome merged in.</summary>
    public IFontHandle IconTextFont { get; }

    public UiTheme(IDalamudPluginInterface pi)
    {
        HeaderFont = pi.UiBuilder.FontAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.Axis18));

        MediumFont = pi.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk =>
            {
                var font = tk.AddDalamudDefaultFont(-1.2f);
                tk.AddFontAwesomeIconFont(new SafeFontConfig
                {
                    SizePx = UiBuilder.DefaultFontSizePx * 1.2f,
                    MergeFont = font,
                });
            }));

        IconTextFont = pi.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            e.OnPreBuild(tk =>
            {
                var font = tk.AddDalamudDefaultFont(-1f);
                tk.AddFontAwesomeIconFont(new SafeFontConfig
                {
                    // Size and spacing were tweaked til they looked decent.
                    SizePx = UiBuilder.DefaultFontSizePx * 0.75f,
                    MergeFont = font,
                    GlyphExtraSpacing = new Vector2(UiBuilder.DefaultFontSizePx * 0.3f, 0f),
                });
            }));
    }

    /// <summary>Completes once every font handle has finished building.</summary>
    public Task WaitFontsReadyAsync(CancellationToken ct) =>
        Task.WhenAll(
            HeaderFont.WaitAsync(ct),
            MediumFont.WaitAsync(ct),
            IconTextFont.WaitAsync(ct));

    public void Dispose()
    {
        HeaderFont.Dispose();
        MediumFont.Dispose();
        IconTextFont.Dispose();
    }
}
