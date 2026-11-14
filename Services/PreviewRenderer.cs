using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;
namespace DowntimeGreeting.Services;

/// <summary>
/// Рендерит содержимое окна WPF в PNG-файл без захвата экрана.
/// </summary>
/// <remarks>
/// Используется служебным режимом <c>--render-preview</c> для генерации изображений
/// документации: окно показывается за пределами рабочего стола, его визуальное дерево
/// отрисовывается в <see cref="RenderTargetBitmap"/>, после чего окно закрывается.
/// На экране ничего не появляется, посторонние приложения в кадр не попадают.
/// </remarks>
public static class PreviewRenderer
{
    /// <summary>Координата, выносящая окно за пределы любого рабочего стола.</summary>
    public const double OffscreenCoordinate = -30000;

    /// <summary>
    /// Сохраняет визуальное дерево окна в PNG.
    /// </summary>
    /// <param name="window">Окно, которое уже показано и разложено (может быть за пределами экрана).</param>
    /// <param name="filePath">Путь к сохраняемому PNG-файлу.</param>
    /// <param name="scale">Масштаб рендера (2.0 — удвоенная плотность для документации).</param>
    /// <param name="backdrop">
    /// Цвет подложки под содержимым окна. Плашка полупрозрачна: без подложки
    /// <see cref="RenderTargetBitmap"/> смешает стекло с прозрачным фоном и получится
    /// «плоский» серый цвет вместо настоящего вида на рабочем столе.
    /// </param>
    /// <exception cref="ArgumentNullException">Окно или путь равны <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">У окна нет визуального содержимого.</exception>
    public static void RenderWindow(Window window, string filePath, double scale = 2.0, Color? backdrop = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        // Корневой визуал окна включает внешние отступы (тень), поэтому берём его,
        // а не window.Content — иначе края кадра обрежутся.
        Visual root = VisualTreeHelper.GetChild(window, 0) is Visual child ? child : window;

        if (root is FrameworkElement rootElement)
        {
            rootElement.UpdateLayout();
        }

        double width = (root as FrameworkElement)?.ActualWidth ?? window.ActualWidth;
        double height = (root as FrameworkElement)?.ActualHeight ?? window.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException($"Некорректный размер окна: {width}x{height}.");
        }

        double dpi = 96d * scale;
        int pixelWidth = (int)Math.Ceiling(width * scale);
        int pixelHeight = (int)Math.Ceiling(height * scale);

        // VisualBrush переносит дерево в отдельный DrawingVisual — так в кадр попадает
        // ровно содержимое окна, без обоев рабочего стола и соседних приложений.
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            if (backdrop is { } backdropColor)
            {
                context.DrawRectangle(new SolidColorBrush(backdropColor), null, new Rect(0, 0, width, height));
            }

            var brush = new VisualBrush(root)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height)
            };

            context.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using (FileStream stream = File.Create(filePath))
        {
            encoder.Save(stream);
        }

        Log.Information(
            "Превью сохранено: {Path} ({Width}x{Height} px, масштаб {Scale})",
            filePath,
            pixelWidth,
            pixelHeight,
            scale);
    }
}