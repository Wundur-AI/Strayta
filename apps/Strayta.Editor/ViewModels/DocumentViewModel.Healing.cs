using Strayta.Core;
using Strayta.Core.Painting;

namespace Strayta.Editor.ViewModels;

/// <summary>Spot Healing's Type menu.</summary>
public enum SpotHealType
{
    /// <summary>Patch synthesis over the stroke from the surroundings (PatchMatch, coarse to fine), healed in.</summary>
    ContentAware,

    /// <summary>A texture of patches from the ring around the stroke, healed in.</summary>
    CreateTexture,

    /// <summary>A nearby area whose surroundings match, healed in (SpotHealing).</summary>
    ProximityMatch,
}

// The Spot Healing Brush's three types, and the heal modes (called from DocumentViewModel.Retouch.cs on release).
public sealed partial class DocumentViewModel
{
    /// <summary>The type the last Spot Healing stroke actually used (Content-Aware falls back to Proximity Match when there is nothing to sample).</summary>
    internal SpotHealType? LastSpotType { get; private set; }

    /// <summary>
    /// The healed pixels for a Spot Healing stroke, over its bounds (in the background, on release). Replace skips the
    /// color adaptation and paints the synthesized (or matched) pixels as they are.
    /// </summary>
    private PixelSource SpotHeal(PixelSource image, PaintStroke stroke, PixelRect canvas, RetouchStroke r, out (int Dx, int Dy)? found)
    {
        found = null;
        bool replace = r.HealMode == HealMode.Replace;
        if (r.SpotType is SpotHealType.ContentAware or SpotHealType.CreateTexture)
        {
            var kind = r.SpotType == SpotHealType.CreateTexture ? SynthesisKind.CreateTexture : SynthesisKind.ContentAware;
            var patch = replace
                ? PatchSynthesis.Synthesize(image, Healing.Coverage(stroke), stroke.Bounds, canvas, new SynthesisOptions
                {
                    Kind = kind,
                    SampleMargin = Math.Clamp(3 * Math.Max(stroke.Bounds.Width, stroke.Bounds.Height), 48, 600),
                })
                : PatchSynthesis.HealStroke(image, stroke, canvas, kind, r.Heal);
            if (patch is not null)
            {
                LastSpotType = r.SpotType;
                return patch;
            }
        }

        LastSpotType = SpotHealType.ProximityMatch;
        found = SpotHealing.FindSource(image, stroke, canvas);
        if (found is { } o)
            return replace ? new CloneSource(o.Dx, o.Dy, image).Placed() : Healing.HealStroke(image, o.Dx, o.Dy, stroke, canvas, r.Heal);
        // Nothing fits (a tiny image): fill from the surroundings alone.
        return Healing.Heal(image, PixelSource.FromFloats([], image.ColorChannels, PixelRect.Empty), 0, 0, Healing.Coverage(stroke), stroke.Bounds, canvas, r.Heal);
    }
}
