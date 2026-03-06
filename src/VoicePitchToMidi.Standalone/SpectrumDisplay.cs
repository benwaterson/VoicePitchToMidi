using System.Windows;
using System.Windows.Media;

namespace VoicePitchToMidi.Standalone;

public class SpectrumDisplay : FrameworkElement
{
    public static readonly DependencyProperty BinsProperty =
        DependencyProperty.Register(nameof(Bins), typeof(float[]), typeof(SpectrumDisplay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[] Bins
    {
        get => (float[])GetValue(BinsProperty);
        set => SetValue(BinsProperty, value);
    }

    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xD4, 0xAA));
    private static readonly Pen BarPen = new(Brushes.Transparent, 0);

    static SpectrumDisplay()
    {
        BarBrush.Freeze();
        BarPen.Freeze();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var bins = Bins;
        if (bins == null || bins.Length == 0) return;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        int count = bins.Length;
        double barWidth = w / count;

        // Log-scale: map magnitude through log10(1 + mag * scale) / log10(1 + scale)
        const float scale = 1000f;
        float logDenom = MathF.Log10(1f + scale);

        for (int i = 0; i < count; i++)
        {
            float mag = bins[i];
            if (mag <= 0f) continue;

            float normalized = MathF.Log10(1f + mag * scale) / logDenom;
            if (normalized > 1f) normalized = 1f;

            double barHeight = normalized * h;
            double x = i * barWidth;
            double y = h - barHeight;

            dc.DrawRectangle(BarBrush, null, new Rect(x, y, Math.Max(barWidth - 1, 1), barHeight));
        }
    }
}
