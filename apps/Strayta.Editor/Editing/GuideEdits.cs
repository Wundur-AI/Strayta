using Strayta.Core;

namespace Strayta.Editor.Editing;

/// <summary>
/// A change to the document's guides as one history step (New Guide, Move Guide, Delete Guide, Clear Guides, New Guide
/// Layout). Guides are saved in the file, so the step marks the document as changed; it needs no new render.
/// </summary>
public sealed class GuideEdit(Document document, IReadOnlyList<Guide> after, string description) : IEdit
{
    private readonly IReadOnlyList<Guide> _before = document.Guides;

    public string Description { get; } = description;
    public bool ChangesStructure => false;
    public IReadOnlyList<Guide> After { get; } = after;

    public void Do() => document.Guides = After;
    public void Undo() => document.Guides = _before;
}

/// <summary>View › New Guide Layout: columns and rows with gutters inside margins (Photoshop's dialog; blank sizes fill the space).</summary>
public sealed record GuideLayout
{
    public int Columns { get; init; }
    public double? ColumnWidth { get; init; }
    public double ColumnGutter { get; init; }
    public int Rows { get; init; }
    public double? RowHeight { get; init; }
    public double RowGutter { get; init; }
    public double MarginTop { get; init; }
    public double MarginLeft { get; init; }
    public double MarginBottom { get; init; }
    public double MarginRight { get; init; }
    public bool HasMargins { get; init; }
    public bool CenterColumns { get; init; }

    /// <summary>The guides this layout makes on a <paramref name="width"/>×<paramref name="height"/> document (all in pixels).</summary>
    public IReadOnlyList<Guide> GuidesFor(int width, int height)
    {
        var guides = new List<Guide>();
        double left = HasMargins ? MarginLeft : 0, right = width - (HasMargins ? MarginRight : 0);
        double top = HasMargins ? MarginTop : 0, bottom = height - (HasMargins ? MarginBottom : 0);
        if (HasMargins)
        {
            guides.Add(new Guide(GuideOrientation.Vertical, left));
            guides.Add(new Guide(GuideOrientation.Vertical, right));
            guides.Add(new Guide(GuideOrientation.Horizontal, top));
            guides.Add(new Guide(GuideOrientation.Horizontal, bottom));
        }
        Divide(guides, GuideOrientation.Vertical, left, right, Columns, ColumnWidth, ColumnGutter, CenterColumns);
        Divide(guides, GuideOrientation.Horizontal, top, bottom, Rows, RowHeight, RowGutter, center: false);
        // Edges that coincide (no gutter, or a column against a margin) are one guide.
        return guides.Select(g => g with { Position = Math.Round(g.Position * 32) / 32 }).Distinct().ToList();
    }

    private static void Divide(List<Guide> guides, GuideOrientation orientation, double from, double to, int count, double? size, double gutter, bool center)
    {
        if (count <= 0 || to <= from) return;
        gutter = Math.Max(0, gutter);
        double cell = size is > 0 ? size.Value : (to - from - gutter * (count - 1)) / count;
        if (cell <= 0) return;
        double total = cell * count + gutter * (count - 1);
        double start = center ? from + (to - from - total) / 2 : from;
        for (int i = 0; i < count; i++)
        {
            double a = start + i * (cell + gutter);
            guides.Add(new Guide(orientation, a));
            guides.Add(new Guide(orientation, a + cell));
        }
    }
}
