using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// The gradient presets every gradient picker shows: Photoshop's basic ones (which follow the foreground and
/// background colors), a set of classic multi-color ones, then the person's own (<see cref="UserPresets"/>).
/// </summary>
public static class GradientPresets
{
    private static GradientColorStop Fg(float at) => new(at, 0.5f, RgbColor.Black) { Kind = GradientStopKind.Foreground };
    private static GradientColorStop Bg(float at) => new(at, 0.5f, new RgbColor(1, 1, 1)) { Kind = GradientStopKind.Background };
    private static GradientColorStop C(float at, int rgb) => new(at, 0.5f, new RgbColor((rgb >> 16 & 255) / 255f, (rgb >> 8 & 255) / 255f, (rgb & 255) / 255f));
    private static GradientOpacityStop O(float at, float opacity) => new(at, 0.5f, opacity);
    private static readonly GradientOpacityStop[] Opaque = [O(0, 1), O(1, 1)];

    private static Gradient Make(string name, GradientColorStop[] colors, GradientOpacityStop[]? opacities = null) =>
        new(colors, opacities ?? Opaque) { Name = name };

    public static Gradient ForegroundToBackground { get; } = Make("Foreground to Background", [Fg(0), Bg(1)]);
    public static Gradient ForegroundToTransparent { get; } = Make("Foreground to Transparent", [Fg(0), Fg(1)], [O(0, 1), O(1, 0)]);
    public static Gradient BlackToWhite { get; } = Make("Black, White", [C(0, 0x000000), C(1, 0xFFFFFF)]);

    /// <summary>Built-in presets, basics first.</summary>
    public static IReadOnlyList<Gradient> BuiltIn { get; } =
    [
        ForegroundToBackground,
        ForegroundToTransparent,
        BlackToWhite,
        Make("Red, Green", [C(0, 0xE11B22), C(1, 0x00A651)]),
        Make("Violet, Orange", [C(0, 0x29166F), C(1, 0xF7941D)]),
        Make("Blue, Red, Yellow", [C(0, 0x0A4DA2), C(0.5f, 0xE0201B), C(1, 0xFCEE21)]),
        Make("Blue, Yellow, Blue", [C(0, 0x0B3D91), C(0.5f, 0xFFE600), C(1, 0x0B3D91)]),
        Make("Orange, Yellow, Orange", [C(0, 0xF15A24), C(0.5f, 0xFFF200), C(1, 0xF15A24)]),
        Make("Violet, Green, Orange", [C(0, 0x6A1B9A), C(0.5f, 0x2E9D48), C(1, 0xF7931E)]),
        Make("Copper", [C(0, 0x8C4B26), C(0.35f, 0xE8A66B), C(0.65f, 0x7A3A17), C(1, 0xF2C29B)]),
        Make("Chrome", [C(0, 0x2C3E50), C(0.48f, 0xE8F1F8), C(0.5f, 0x4A3A28), C(0.75f, 0xB08D5B), C(1, 0xFFFFFF)]),
        Make("Spectrum", [C(0, 0xFF0000), C(1 / 6f, 0xFFFF00), C(2 / 6f, 0x00FF00), C(3 / 6f, 0x00FFFF), C(4 / 6f, 0x0000FF), C(5 / 6f, 0xFF00FF), C(1, 0xFF0000)]),
        Make("Transparent Rainbow", [C(0, 0xFF0000), C(0.2f, 0xFFFF00), C(0.4f, 0x00FF00), C(0.6f, 0x00FFFF), C(0.8f, 0x0000FF), C(1, 0xFF00FF)],
            [O(0, 0), O(0.15f, 1), O(0.85f, 1), O(1, 0)]),
        Make("Transparent Stripes", [Fg(0), Fg(1)],
            [O(0, 1), O(0.1f, 1), O(0.1f, 0), O(0.2f, 0), O(0.2f, 1), O(0.3f, 1), O(0.3f, 0), O(0.4f, 0), O(0.4f, 1), O(0.5f, 1),
             O(0.5f, 0), O(0.6f, 0), O(0.6f, 1), O(0.7f, 1), O(0.7f, 0), O(0.8f, 0), O(0.8f, 1), O(0.9f, 1), O(0.9f, 0), O(1, 0)]),
    ];

    /// <summary>Built-in presets followed by the person's own.</summary>
    public static IEnumerable<Gradient> All => BuiltIn.Concat(UserPresets.Shared.Gradients);

    public static bool IsUserPreset(Gradient g) => UserPresets.Shared.Gradients.Any(u => ReferenceEquals(u, g));
}
