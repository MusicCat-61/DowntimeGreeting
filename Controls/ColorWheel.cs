using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DowntimeGreeting.Controls;

/// <summary>
/// Простой HSV-пикер акцентного цвета: цветовое колесо (тон и насыщенность) и ползунок яркости.
/// </summary>
/// <remarks>
/// Внешних UI-библиотек не используется: колесо рисуется в <see cref="OnRender"/> через
/// концентрические сектора, яркость — линейный градиент. Клик или перетаскивание по колесу
/// меняют <see cref="SelectedColor"/>; обратное присваивание обновляет положение маркера.
/// </remarks>
public sealed class ColorWheel : Control
{
    /// <summary>Минимальный радиус колеса, px.</summary>
    private const double MinimumRadius = 8;

    /// <summary>Количество секторов при отрисовке колеса (чем больше, тем плавнее).</summary>
    private const int HueSegments = 360;

    /// <summary>Радиус маркера выбранного цвета, px.</summary>
    private const double MarkerRadius = 5.5;

    private double _hue;
    private double _saturation = 1d;
    private double _value = 1d;

    private bool _isDragging;
    private bool _isUpdatingFromColor;

    /// <summary>Кэш отрисованного колеса при полной яркости (перерисовывается при смене размера).</summary>
    private DrawingGroup? _wheelDrawing;

    private double _cachedWidth;
    private double _cachedHeight;

    static ColorWheel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ColorWheel),
            new FrameworkPropertyMetadata(typeof(ColorWheel)));
    }

    /// <summary>Инициализирует новый экземпляр <see cref="ColorWheel"/>.</summary>
    public ColorWheel()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    /// <summary>Свойство зависимости <see cref="SelectedColor"/>.</summary>
    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(
            nameof(SelectedColor),
            typeof(Color),
            typeof(ColorWheel),
            new FrameworkPropertyMetadata(
                Colors.MediumSlateBlue,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnSelectedColorChanged));

    /// <summary>Выбранный цвет.</summary>
    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>Текущая яркость (0…1) — используется ползунком яркости.</summary>
    public double Brightness => _value;

    /// <summary>Текущий тон в градусах (0…360).</summary>
    public double Hue => _hue;

    /// <summary>Текущая насыщенность (0…1).</summary>
    public double Saturation => _saturation;

    /// <summary>
    /// Задаёт яркость, сохраняя тон и насыщенность.
    /// </summary>
    /// <param name="value">Яркость 0…1.</param>
    public void SetBrightness(double value)
    {
        _value = Math.Clamp(value, 0d, 1d);
        ApplyColor();
    }

    /// <summary>Событие изменения выбранного цвета (пользователем или программно).</summary>
    public event EventHandler? SelectedColorChanged;

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);

        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        double radius = Math.Min(width, height) / 2;
        if (radius < MinimumRadius)
        {
            return;
        }

        var center = new Point(width / 2, height / 2);
        DrawWheel(drawingContext, center, radius);
        DrawMarker(drawingContext, center, radius);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        _isDragging = true;
        CaptureMouse();
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_isDragging)
        {
            UpdateFromPoint(e.GetPosition(this));
        }
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
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
    /// Рисует цветовое колесо: сектора по тону с радиальным градиентом к белому в центре.
    /// </summary>
    /// <param name="drawingContext">Контекст отрисовки.</param>
    /// <param name="center">Центр колеса.</param>
    /// <param name="radius">Радиус колеса.</param>
    private void DrawWheel(DrawingContext drawingContext, Point center, double radius)
    {
        if (_wheelDrawing is null || Math.Abs(_cachedWidth - ActualWidth) > 0.5 || Math.Abs(_cachedHeight - ActualHeight) > 0.5)
        {
            _wheelDrawing = BuildWheelDrawing(center, radius);
            _cachedWidth = ActualWidth;
            _cachedHeight = ActualHeight;
        }

        drawingContext.DrawDrawing(_wheelDrawing);

        // Яркость применяется затемнением: HSV(h, s, v) = HSV(h, s, 1) × v,
        // поэтому вместо перерисовки 360 секторов достаточно чёрной плёнки.
        if (_value < 0.999)
        {
            var shade = new SolidColorBrush(Color.FromArgb((byte)Math.Round((1d - _value) * 255), 0, 0, 0));
            shade.Freeze();
            drawingContext.DrawEllipse(shade, null, center, radius, radius);
        }
    }

    /// <summary>
    /// Строит геометрию колеса при полной яркости для последующего кэширования.
    /// </summary>
    /// <param name="center">Центр колеса.</param>
    /// <param name="radius">Радиус колеса.</param>
    /// <returns>Готовая группа отрисовки.</returns>
    private static DrawingGroup BuildWheelDrawing(Point center, double radius)
    {
        var drawing = new DrawingGroup();

        for (int segment = 0; segment < HueSegments; segment++)
        {
            double startAngle = 360d * segment / HueSegments;
            // Небольшой перехлёст убирает светлые швы между секторами.
            double endAngle = (360d * (segment + 1) / HueSegments) + 0.8;

            var pureColor = FromHsv(startAngle, 1d, 1d);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(center, isFilled: true, isClosed: true);
                context.ArcTo(
                    PointOnCircle(center, radius, endAngle),
                    new Size(radius, radius),
                    0,
                    isLargeArc: false,
                    SweepDirection.Clockwise,
                    isStroked: false,
                    isSmoothJoin: false);
                context.ArcTo(
                    PointOnCircle(center, radius, startAngle),
                    new Size(radius, radius),
                    0,
                    isLargeArc: false,
                    SweepDirection.Counterclockwise,
                    isStroked: false,
                    isSmoothJoin: false);
            }

            geometry.Freeze();

            // Радиальный градиент задаётся в координатах контрола, поэтому переводим
            // кисть в относительные координаты колеса через Viewbox.
            var brush = new RadialGradientBrush
            {
                GradientOrigin = new Point(center.X, center.Y),
                Center = new Point(center.X, center.Y),
                RadiusX = radius,
                RadiusY = radius,
                MappingMode = BrushMappingMode.Absolute
            };
            brush.GradientStops.Add(new GradientStop(Colors.White, 0d));
            brush.GradientStops.Add(new GradientStop(pureColor, 1d));
            brush.Freeze();

            drawing.Children.Add(new GeometryDrawing(brush, null, geometry));
        }

        drawing.Freeze();
        return drawing;
    }

    /// <summary>
    /// Рисует маркер выбранного цвета.
    /// </summary>
    /// <param name="drawingContext">Контекст отрисовки.</param>
    /// <param name="center">Центр колеса.</param>
    /// <param name="radius">Радиус колеса.</param>
    private void DrawMarker(DrawingContext drawingContext, Point center, double radius)
    {
        double markerRadius = radius * _saturation;
        Point markerCenter = PointOnCircle(center, markerRadius, _hue);

        var outerRing = new Pen(Brushes.White, 2.0);
        outerRing.Freeze();
        var innerRingBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x00, 0x00, 0x00));
        innerRingBrush.Freeze();
        var innerRing = new Pen(innerRingBrush, 1.0);
        innerRing.Freeze();

        var markerBrush = new SolidColorBrush(SelectedColor);
        markerBrush.Freeze();

        drawingContext.DrawEllipse(markerBrush, outerRing, markerCenter, MarkerRadius, MarkerRadius);
        drawingContext.DrawEllipse(null, innerRing, markerCenter, MarkerRadius + 1.5, MarkerRadius + 1.5);
    }

    /// <summary>
    /// Пересчитывает тон и насыщенность по позиции указателя.
    /// </summary>
    /// <param name="point">Позиция указателя внутри контрола.</param>
    private void UpdateFromPoint(Point point)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        double radius = Math.Min(width, height) / 2;
        if (radius < MinimumRadius)
        {
            return;
        }

        var center = new Point(width / 2, height / 2);
        double dx = point.X - center.X;
        double dy = point.Y - center.Y;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));

        double saturation = Math.Clamp(distance / radius, 0d, 1d);
        double hue = (Math.Atan2(dy, dx) * 180d / Math.PI) + 360d;
        hue %= 360d;

        _hue = hue;
        _saturation = saturation;

        ApplyColor();
    }

    /// <summary>
    /// Записывает текущий HSV в <see cref="SelectedColor"/> и обновляет отрисовку.
    /// </summary>
    private void ApplyColor()
    {
        _isUpdatingFromColor = true;
        try
        {
            SelectedColor = FromHsv(_hue, _saturation, _value);
        }
        finally
        {
            _isUpdatingFromColor = false;
        }

        InvalidateVisual();
        SelectedColorChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Обработчик изменения <see cref="SelectedColor"/>: обновляет HSV и маркер.
    /// </summary>
    /// <param name="d">Объект свойства зависимости.</param>
    /// <param name="e">Аргументы изменения.</param>
    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ColorWheel wheel || wheel._isUpdatingFromColor)
        {
            return;
        }

        var color = (Color)e.NewValue;
        ToHsv(color, out double hue, out double saturation, out double value);

        // Оттенок у серых цветов не определён: сохраняем прежний, чтобы маркер не прыгал.
        if (saturation > 0.001)
        {
            wheel._hue = hue;
        }

        wheel._saturation = saturation;
        wheel._value = value;

        wheel.InvalidateVisual();
        wheel.SelectedColorChanged?.Invoke(wheel, EventArgs.Empty);
    }

    /// <summary>
    /// Вычисляет точку на окружности.
    /// </summary>
    /// <param name="center">Центр окружности.</param>
    /// <param name="radius">Радиус.</param>
    /// <param name="angleDegrees">Угол в градусах (0 — вправо, по часовой стрелке).</param>
    /// <returns>Точка на окружности.</returns>
    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + (radius * Math.Cos(radians)), center.Y + (radius * Math.Sin(radians)));
    }

    /// <summary>
    /// Преобразует HSV в RGB.
    /// </summary>
    /// <param name="hue">Тон, градусы 0…360.</param>
    /// <param name="saturation">Насыщенность 0…1.</param>
    /// <param name="value">Яркость 0…1.</param>
    /// <returns>Цвет RGB.</returns>
    public static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360d) + 360d) % 360d;
        saturation = Math.Clamp(saturation, 0d, 1d);
        value = Math.Clamp(value, 0d, 1d);

        double c = value * saturation;
        double x = c * (1 - Math.Abs(((hue / 60d) % 2) - 1));
        double m = value - c;

        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    /// <summary>
    /// Преобразует RGB в HSV.
    /// </summary>
    /// <param name="color">Цвет RGB.</param>
    /// <param name="hue">Тон, градусы 0…360.</param>
    /// <param name="saturation">Насыщенность 0…1.</param>
    /// <param name="value">Яркость 0…1.</param>
    public static void ToHsv(Color color, out double hue, out double saturation, out double value)
    {
        double r = color.R / 255d;
        double g = color.G / 255d;
        double b = color.B / 255d;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        value = max;
        saturation = max <= 0d ? 0d : delta / max;

        if (delta <= 0d)
        {
            hue = 0d;
            return;
        }

        hue = max switch
        {
            _ when Math.Abs(max - r) < double.Epsilon => 60d * (((g - b) / delta) % 6d),
            _ when Math.Abs(max - g) < double.Epsilon => 60d * (((b - r) / delta) + 2d),
            _ => 60d * (((r - g) / delta) + 4d)
        };

        if (hue < 0d)
        {
            hue += 360d;
        }
    }
}