using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Strayta.Core;
using Strayta.Core.Painting;
using Strayta.Core.Selection;
using Strayta.Editor.Controls;

namespace Strayta.Editor.ViewModels;

/// <summary>Edit › Fill's Contents choices, in Photoshop's order.</summary>
public enum FillContents
{
    Foreground,
    Background,
    Color,
    ContentAware,
    Pattern,
    Black,
    Gray50,
    White,
}

/// <summary>A row of the Contents menu; Content-Aware is disabled until an implementation is plugged in.</summary>
public sealed record FillContentOption(string Name, FillContents Contents, bool IsEnabled);

/// <summary>
/// Photoshop's Fill dialog: Contents (Foreground Color, Background Color, Color…, Content-Aware, Pattern, Black, 50%
/// Gray, White), and Blending: Mode, Opacity, Preserve Transparency.
/// </summary>
public sealed partial class FillDialogViewModel : ObservableObject
{
    public FillDialogViewModel(IEnumerable<Pattern>? documentPatterns) => DocumentPatterns = documentPatterns?.ToList();

    public IReadOnlyList<Pattern>? DocumentPatterns { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentOptions))]
    public partial bool ContentAwareAvailable { get; set; }

    public IReadOnlyList<FillContentOption> ContentOptions =>
    [
        new("Foreground Color", FillContents.Foreground, true),
        new("Background Color", FillContents.Background, true),
        new("Color…", FillContents.Color, true),
        new("Content-Aware", FillContents.ContentAware, ContentAwareAvailable),
        new("Pattern", FillContents.Pattern, true),
        new("Black", FillContents.Black, true),
        new("50% Gray", FillContents.Gray50, true),
        new("White", FillContents.White, true),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContentsIndex), nameof(UsesPattern), nameof(UsesColor), nameof(BlendingEnabled))]
    public partial FillContents Contents { get; set; }

    public int ContentsIndex
    {
        get => (int)Contents;
        set
        {
            if (value < 0 || value >= ContentOptions.Count || !ContentOptions[value].IsEnabled) return;
            Contents = (FillContents)value;
        }
    }

    public bool UsesPattern => Contents == FillContents.Pattern;
    public bool UsesColor => Contents == FillContents.Color;

    /// <summary>Content-Aware brings its own blending, as in Photoshop.</summary>
    public bool BlendingEnabled => Contents != FillContents.ContentAware;

    [ObservableProperty] public partial Color CustomColor { get; set; } = Colors.Gray;
    [ObservableProperty] public partial Pattern? Pattern { get; set; } = PatternLibrary.BuiltIn[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIndex))]
    public partial PaintMode Mode { get; set; } = PaintMode.Normal;

    public int ModeIndex { get => PaintModeNames.IndexOf(Mode); set { if (PaintModeNames.At(value) is { } m) Mode = m; } }

    /// <summary>Percent.</summary>
    [ObservableProperty] public partial double Opacity { get; set; } = 100;
    [ObservableProperty] public partial bool PreserveTransparency { get; set; }

    /// <summary>The same settings for the next time the dialog opens (on another document, with its patterns).</summary>
    public FillDialogViewModel Copy(DocumentViewModel doc) => new(doc.Model.Patterns)
    {
        Contents = Contents == FillContents.ContentAware ? FillContents.Foreground : Contents,
        CustomColor = CustomColor,
        Pattern = Pattern,
        Mode = Mode,
        Opacity = Opacity,
        PreserveTransparency = PreserveTransparency,
    };

    public FillOptions ToOptions(RgbColor foreground, RgbColor background)
    {
        FillSource source = Contents switch
        {
            FillContents.Background => new FillSource.Color(background),
            FillContents.Color => new FillSource.Color(new RgbColor(CustomColor.R / 255f, CustomColor.G / 255f, CustomColor.B / 255f)),
            FillContents.Pattern when Pattern is { } p => new FillSource.Tiled(p),
            FillContents.Black => new FillSource.Color(RgbColor.Black),
            // Photoshop's 50% gray is 128 in 8-bit documents.
            FillContents.Gray50 => new FillSource.Color(new RgbColor(128 / 255f, 128 / 255f, 128 / 255f)),
            FillContents.White => new FillSource.Color(new RgbColor(1, 1, 1)),
            _ => new FillSource.Color(foreground),
        };
        return new FillOptions(source)
        {
            Mode = Mode,
            Opacity = (float)Math.Clamp(Opacity / 100, 0, 1),
            PreserveTransparency = PreserveTransparency,
        };
    }
}

/// <summary>Photoshop's Stroke dialog: Width, Color, Location (Inside / Center / Outside), Mode, Opacity, Preserve Transparency.</summary>
public sealed partial class StrokeDialogViewModel : ObservableObject
{
    /// <summary>Pixels, 1..250.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WidthValue))]
    public partial int Width { get; set; } = 1;

    partial void OnWidthChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, SelectionStroke.MaxWidth);
        if (clamped != value) Width = clamped;
    }

    [ObservableProperty] public partial Color Color { get; set; } = Colors.Black;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInside), nameof(IsCenter), nameof(IsOutside))]
    public partial StrokeLocation Location { get; set; } = StrokeLocation.Center;

    public bool IsInside { get => Location == StrokeLocation.Inside; set { if (value) Location = StrokeLocation.Inside; } }
    public bool IsCenter { get => Location == StrokeLocation.Center; set { if (value) Location = StrokeLocation.Center; } }
    public bool IsOutside { get => Location == StrokeLocation.Outside; set { if (value) Location = StrokeLocation.Outside; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeIndex))]
    public partial PaintMode Mode { get; set; } = PaintMode.Normal;

    public int ModeIndex { get => PaintModeNames.IndexOf(Mode); set { if (PaintModeNames.At(value) is { } m) Mode = m; } }

    /// <summary>Percent.</summary>
    [ObservableProperty] public partial double Opacity { get; set; } = 100;
    [ObservableProperty] public partial bool PreserveTransparency { get; set; }

    /// <summary>Width as a double for the number field.</summary>
    public double WidthValue { get => Width; set => Width = (int)Math.Round(value); }

    public StrokeDialogViewModel Copy() => new()
    {
        Width = Width, Color = Color, Location = Location, Mode = Mode, Opacity = Opacity, PreserveTransparency = PreserveTransparency,
    };

    public FillOptions ToOptions() => new(new FillSource.Color(new RgbColor(Color.R / 255f, Color.G / 255f, Color.B / 255f)))
    {
        Mode = Mode,
        Opacity = (float)Math.Clamp(Opacity / 100, 0, 1),
        PreserveTransparency = PreserveTransparency,
    };
}
