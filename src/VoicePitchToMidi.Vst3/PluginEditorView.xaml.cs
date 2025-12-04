using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioPlugSharp;
using WpfColor = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;

namespace VoicePitchToMidi.Vst3;

public partial class PluginEditorView : System.Windows.Controls.UserControl
{
    private readonly VoicePitchToMidiPlugin _plugin;
    private readonly DispatcherTimer _updateTimer;

    // Colors
    private static readonly WpfColor AccentColor = WpfColor.FromRgb(0x00, 0xD4, 0xAA);
    private static readonly WpfColor TextPrimary = WpfColor.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly WpfColor TextSecondary = WpfColor.FromRgb(0x80, 0x80, 0x80);
    private static readonly WpfColor BgTrack = WpfColor.FromRgb(0x2A, 0x2A, 0x2A);

    public PluginEditorView(VoicePitchToMidiPlugin plugin)
    {
        InitializeComponent();

        _plugin = plugin;

        // Add parameter controls
        AddParameterControls();

        // Update display periodically
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30fps
        };
        _updateTimer.Tick += UpdateDisplay;
        _updateTimer.Start();

        Unloaded += (s, e) => _updateTimer.Stop();
    }

    private void AddParameterControls()
    {
        foreach (var param in _plugin.Parameters)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            // Header row with label and value
            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = param.Name.ToUpperInvariant(),
                Foreground = new SolidColorBrush(TextSecondary),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(label, 0);
            headerGrid.Children.Add(label);

            var valueText = new TextBlock
            {
                Text = FormatValue(param),
                Foreground = new SolidColorBrush(AccentColor),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Tag = param
            };
            Grid.SetColumn(valueText, 1);
            headerGrid.Children.Add(valueText);

            panel.Children.Add(headerGrid);

            // Custom slider track
            var sliderContainer = new Grid { Height = 24 };

            // Track background
            var trackBg = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(BgTrack),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            sliderContainer.Children.Add(trackBg);

            // Track fill (will be updated)
            var trackFill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(AccentColor),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Tag = "fill"
            };
            sliderContainer.Children.Add(trackFill);

            // Actual slider (transparent, for interaction)
            var slider = new Slider
            {
                Minimum = param.MinValue,
                Maximum = param.MaxValue,
                Value = param.EditValue,
                Tag = param,
                Background = WpfBrushes.Transparent,
                Foreground = WpfBrushes.Transparent,
                BorderBrush = WpfBrushes.Transparent,
                Opacity = 0.01 // Nearly invisible but still interactive
            };
            slider.ValueChanged += (s, e) => OnSliderValueChanged(s, e, trackFill, valueText);
            sliderContainer.Children.Add(slider);

            // Update initial fill width
            UpdateTrackFill(trackFill, param.EditValue, param.MinValue, param.MaxValue, sliderContainer.ActualWidth > 0 ? sliderContainer.ActualWidth : 350);

            // Update fill when container is loaded
            sliderContainer.Loaded += (s, e) =>
            {
                UpdateTrackFill(trackFill, slider.Value, param.MinValue, param.MaxValue, sliderContainer.ActualWidth);
            };

            panel.Children.Add(sliderContainer);
            ParametersPanel.Children.Add(panel);
        }
    }

    private static string FormatValue(AudioPluginParameter param)
    {
        try
        {
            return string.Format(param.ValueFormat ?? "{0:F2}", param.EditValue);
        }
        catch
        {
            return param.EditValue.ToString("F2");
        }
    }

    private void OnSliderValueChanged(object? sender, RoutedPropertyChangedEventArgs<double> e, Border trackFill, TextBlock valueText)
    {
        if (sender is Slider slider && slider.Tag is AudioPluginParameter param)
        {
            param.EditValue = e.NewValue;

            // Update value display
            valueText.Text = FormatValue(param);

            // Update track fill
            var container = slider.Parent as Grid;
            if (container != null)
            {
                UpdateTrackFill(trackFill, e.NewValue, param.MinValue, param.MaxValue, container.ActualWidth);
            }
        }
    }

    private static void UpdateTrackFill(Border trackFill, double value, double min, double max, double containerWidth)
    {
        if (containerWidth <= 0) containerWidth = 350;
        double percent = (value - min) / (max - min);
        trackFill.Width = Math.Max(4, percent * containerWidth);
    }

    private void UpdateDisplay(object? sender, EventArgs e)
    {
        // Update note display
        string noteName = _plugin.CurrentNoteName;
        NoteDisplay.Text = noteName;

        // Glow effect intensity based on confidence
        float conf = _plugin.CurrentConfidence;
        if (NoteDisplay.Effect is System.Windows.Media.Effects.DropShadowEffect glow)
        {
            glow.Opacity = conf * 0.6;
        }

        // Update frequency
        FrequencyDisplay.Text = $"{_plugin.CurrentFrequency:F1} Hz";

        // Update confidence
        ConfidenceDisplay.Text = $"{conf:P0}";

        // Update confidence bar
        if (ConfidenceBar != null)
        {
            var parent = ConfidenceBar.Parent as Border;
            if (parent != null && parent.ActualWidth > 0)
            {
                ConfidenceBar.Width = conf * parent.ActualWidth;
            }
        }
    }
}
