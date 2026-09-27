using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NbtDiff.App.ViewModels;

namespace NbtDiff.App.Controls;

/// <summary>
/// The gutter state marker shared by every view (#9): a filled chip for a confirmed state, a dashed
/// outline for "probably different", a faint <c>=</c> for same, a small dot for a roll-up (a folder or
/// container with changes inside). Drawn directly so it needs no template.
/// </summary>
public sealed class StateChip : Control
{
    public static readonly StyledProperty<StateKind> StateProperty = AvaloniaProperty.Register<StateChip, StateKind>(nameof(State));
    public static readonly StyledProperty<string?> GlyphProperty = AvaloniaProperty.Register<StateChip, string?>(nameof(Glyph));
    public static readonly StyledProperty<bool> RollupProperty = AvaloniaProperty.Register<StateChip, bool>(nameof(Rollup));

    static StateChip()
    {
        AffectsRender<StateChip>(StateProperty, GlyphProperty, RollupProperty);
    }

    public StateKind State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool Rollup { get => GetValue(RollupProperty); set => SetValue(RollupProperty, value); }

    private const double ChipWidth = 22, ChipHeight = 16;

    protected override Size MeasureOverride(Size availableSize) => new(ChipWidth, ChipHeight);

    public override void Render(DrawingContext context)
    {
        var state = State;
        if (state == StateKind.None) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var box = new Rect(center.X - ChipWidth / 2, center.Y - ChipHeight / 2, ChipWidth, ChipHeight);
        var color = Brush($"State.{state}");

        if (Rollup)
        {
            context.DrawEllipse(color, null, center, 3.5, 3.5);
            return;
        }
        switch (state)
        {
            case StateKind.Same:
                DrawGlyph(context, Glyph ?? "=", Brush("App.TextDim"), center, opacity: 0.45);
                return;
            case StateKind.Pending:
                context.DrawRectangle(null, new Pen(Brush("App.ControlBorder"), 1), box.Deflate(0.5), 3, 3);
                DrawGlyph(context, Glyph ?? "…", Brush("App.TextMuted"), center);
                return;
            case StateKind.ProbablyDifferent:
                context.DrawRectangle(null, new Pen(color, 1, new DashStyle([2, 2], 0)), box.Deflate(0.5), 3, 3);
                DrawGlyph(context, Glyph ?? "≠?", color, center);
                return;
            default:
                context.DrawRectangle(color, null, box, 3, 3);
                DrawGlyph(context, Glyph ?? "", Brushes.White, center);
                return;
        }
    }

    private void DrawGlyph(DrawingContext context, string glyph, IBrush brush, Point center, double opacity = 1)
    {
        if (glyph.Length == 0) return;
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 11, brush);
        using (context.PushOpacity(opacity))
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.Gray;
}

/// <summary>The empty side of a one-sided row: diagonal hatching instead of a lone dash (#9).</summary>
public sealed class Hatch : Control
{
    private const double Period = 10;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        IBrush a = Find("App.HatchA"), b = Find("App.HatchB");
        context.FillRectangle(b, bounds);
        var pen = new Pen(a, Period / 2);
        using (context.PushClip(bounds))
            for (double x = -bounds.Height; x < bounds.Width + bounds.Height; x += Period)
                context.DrawLine(pen, new Point(x, bounds.Height), new Point(x + bounds.Height, 0));
    }

    private IBrush Find(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found) && found is IBrush brush ? brush : Brushes.Transparent;
}
