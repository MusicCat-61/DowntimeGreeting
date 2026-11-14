using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DowntimeGreeting.Controls;

/// <summary>
/// Ползунок яркости для <see cref="ColorWheel"/>: показывает градиент от чёрного к выбранному тону.
/// </summary>
public sealed class ValueSlider : Control
{
    /// <summary>Радиус круглого маркера, px.</summary>
    private const double MarkerRadius = 6.5;

    /// <summary>Высота дорожки градиента, px.</summary>
    private const double TrackHeight = 12;

    private bool _isDragging;

    static ValueSlider()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ValueSlider),
            new FrameworkPropertyMetadata(typeof(ValueSlider)));
    }

    /// <summary>Инициализирует новый экземпляр <see cref="ValueSlider"/>.</summary>
    public ValueSlider()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    /// <summary>Свойство зависимости <see cref="Hue"/>.</summary>
    public static readonly DependencyProperty HueProperty =
        DependencyProperty.Register(
            nameof(Hue),
            typeof(double),
            typeof(ValueSlider),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Свойство зависимости <see cref="Saturation"/>.</summary>
    public static readonly DependencyProperty SaturationProperty =
        DependencyProperty.Register(
            nameof(Saturation),
            typeof(double),
            typeof(ValueSlider),
            new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Свойство зависимости <see cref="Value"/>.</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(ValueSlider),
            new FrameworkPropertyMetadata(
                1d,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnValueChanged));

    /// <summary>Тон, градусы 0…360 (для градиента дорожки).</summary>
    public double Hue
    {
        get => (double)GetValue(HueProperty);
        set => SetValue(HueProperty, value);
    }

    /// <summary>Насыщенность 0…1 (для градиента дорожки).</summary>
    public double Saturation
    {
        get => (double)GetValue(SaturationProperty);
        set => SetValue(SaturationProperty, value);
    }

    /// <summary>Яркость 0…1.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Событие изменения яркости.</summary>
    public event EventHandler? ValueChanged;

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        base.OnRender(drawingContext);

        double width = ActualWidth;
        if (width <= 0)
        {
            return;
        }

        double centerY = ActualHeight / 2;
        double trackTop = centerY - (TrackHeight / 2);
        double trackBottom = centerY + (TrackHeight / 2);
        double inset = MarkerRadius;

        Color hueColor = ColorWheel.FromHsv(Hue, Saturation, 1d);
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        gradient.GradientStops.Add(new GradientStop(Colors.Black, 0d));
        gradient.GradientStops.Add(new GradientStop(hueColor, 1d));
        gradient.Freeze();

        var trackRect = new Rect(inset, trackTop, Math.Max(0, width - (2 * inset)), TrackHeight);
        drawingContext.DrawRoundedRectangle(gradient, null, trackRect, TrackHeight / 2, TrackHeight / 2);

        double markerX = inset + (Math.Clamp(Value, 0d, 1d) * (width - (2 * inset)));
        var markerCenter = new Point(markerX, centerY);

        var fill = new SolidColorBrush(ColorWheel.FromHsv(Hue, Saturation, Value));
        fill.Freeze();
        var ring = new Pen(Brushes.White, 2.0);
        ring.Freeze();

        drawingContext.DrawEllipse(fill, ring, markerCenter, MarkerRadius, MarkerRadius);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _isDragging = true;
        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isDragging)
        {
            UpdateFromPoint(e.GetPosition(this));
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        if (_isDragging)
        {
            _isDragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Пересчитывает яркость по позиции указателя.
    /// </summary>
    /// <param name="point">Позиция указателя внутри контрола.</param>
    private void UpdateFromPoint(Point point)
    {
        double usableWidth = ActualWidth - (2 * MarkerRadius);
        if (usableWidth <= 0)
        {
            return;
        }

        Value = Math.Clamp((point.X - MarkerRadius) / usableWidth, 0d, 1d);
    }

    /// <summary>
    /// Обработчик изменения яркости.
    /// </summary>
    /// <param name="d">Объект свойства зависимости.</param>
    /// <param name="e">Аргументы изменения.</param>
    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ValueSlider slider)
        {
            slider.ValueChanged?.Invoke(slider, EventArgs.Empty);
        }
    }
}