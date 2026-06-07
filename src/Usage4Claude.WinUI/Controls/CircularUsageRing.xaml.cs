using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Usage4Claude.WinUI.Controls;

public sealed partial class CircularUsageRing : UserControl
{
    private const float OuterStrokeWidth = 6f;
    private const float InnerStrokeWidth = 12f;
    private const float OuterRadius = 50f;
    private const float InnerRadius = 36f;
    private const float StartAngle = -(float)(Math.PI / 2);

    private const double OuterRingHitMin = OuterRadius - OuterStrokeWidth / 2f; // 47
    private const double OuterRingHitMax = OuterRadius + OuterStrokeWidth / 2f; // 53
    private const double InnerRingHitMin = InnerRadius - InnerStrokeWidth / 2f; // 30
    private const double InnerRingHitMax = InnerRadius + InnerStrokeWidth / 2f; // 42
    private const double RingCenterX = 60.0;
    private const double RingCenterY = 60.0;

    private static readonly Color TrackColor = Color.FromArgb(40, 128, 128, 128);
    private static readonly Color PrimaryColor = Color.FromArgb(255, 76, 175, 80);     // green
    private static readonly Color SecondaryColor = Color.FromArgb(255, 149, 117, 205); // purple

    public static readonly DependencyProperty PrimaryPercentageProperty =
        DependencyProperty.Register(
            nameof(PrimaryPercentage),
            typeof(double),
            typeof(CircularUsageRing),
            new PropertyMetadata(0.0, OnPercentageChanged));

    public static readonly DependencyProperty SecondaryPercentageProperty =
        DependencyProperty.Register(
            nameof(SecondaryPercentage),
            typeof(double),
            typeof(CircularUsageRing),
            new PropertyMetadata(0.0, OnPercentageChanged));

    public double PrimaryPercentage
    {
        get => (double)GetValue(PrimaryPercentageProperty);
        set => SetValue(PrimaryPercentageProperty, value);
    }

    public double SecondaryPercentage
    {
        get => (double)GetValue(SecondaryPercentageProperty);
        set => SetValue(SecondaryPercentageProperty, value);
    }

    private static void OnPercentageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CircularUsageRing ring)
        {
            ring.PrimaryPercentText.Text = $"{100 - ring.PrimaryPercentage:0.#}%";
            ring.RingCanvas.Invalidate();
        }
    }

    public CircularUsageRing()
    {
        InitializeComponent();
    }

    private void RingCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RingCanvas).Position;
        var dx = pos.X - RingCenterX;
        var dy = pos.Y - RingCenterY;
        var dist = Math.Sqrt(dx * dx + dy * dy);

        string? text = null;
        if (dist >= OuterRingHitMin && dist <= OuterRingHitMax)
            text = $"7-day {SecondaryPercentage:0.#}% used";
        else if (dist >= InnerRingHitMin && dist <= InnerRingHitMax)
            text = $"5-hour {PrimaryPercentage:0.#}% used";

        if (text != null)
        {
            RingTooltipText.Text = text;
            RingTooltipPopup.HorizontalOffset = pos.X + 14;
            RingTooltipPopup.VerticalOffset = pos.Y - 28;
            RingTooltipPopup.IsOpen = true;
        }
        else
        {
            RingTooltipPopup.IsOpen = false;
        }
    }

    private void RingCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        RingTooltipPopup.IsOpen = false;
    }

    private void RingCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var center = new Vector2((float)(sender.ActualWidth / 2), (float)(sender.ActualHeight / 2));

        var strokeStyle = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
        };

        // Outer ring — 7-day (secondary), purple — arc shows remaining
        ds.DrawCircle(center, OuterRadius, TrackColor, OuterStrokeWidth);
        DrawArcGeometry(ds, sender, center, OuterRadius, 100 - SecondaryPercentage, SecondaryColor, OuterStrokeWidth, strokeStyle);

        // Inner ring — 5-hour (primary), green — arc shows remaining
        ds.DrawCircle(center, InnerRadius, TrackColor, InnerStrokeWidth);
        DrawArcGeometry(ds, sender, center, InnerRadius, 100 - PrimaryPercentage, PrimaryColor, InnerStrokeWidth, strokeStyle);
    }

    private static void DrawArcGeometry(
        CanvasDrawingSession ds,
        ICanvasResourceCreator resourceCreator,
        Vector2 center,
        float radius,
        double percentage,
        Color color,
        float strokeWidth,
        CanvasStrokeStyle strokeStyle)
    {
        var factor = Math.Clamp(percentage / 100.0, 0.0, 1.0);
        if (factor < 0.001)
        {
            return;
        }

        // Cap just below 2π to avoid full-circle rendering edge case
        var sweep = (float)(2 * Math.PI * factor);
        if (sweep >= (float)(2 * Math.PI) - 0.001f)
        {
            sweep = (float)(2 * Math.PI) - 0.001f;
        }

        // Starting point on the arc (12 o'clock)
        var startX = center.X + radius * (float)Math.Cos(StartAngle);
        var startY = center.Y + radius * (float)Math.Sin(StartAngle);

        using var pathBuilder = new CanvasPathBuilder(resourceCreator);
        pathBuilder.BeginFigure(new Vector2(startX, startY));
        pathBuilder.AddArc(center, radius, radius, StartAngle, sweep);
        pathBuilder.EndFigure(CanvasFigureLoop.Open);

        using var geometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.DrawGeometry(geometry, color, strokeWidth, strokeStyle);
    }
}
