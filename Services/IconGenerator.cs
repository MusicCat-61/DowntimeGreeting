using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace DowntimeGreeting.Services;

/// <summary>
/// Генерирует многоразмерную иконку приложения (<c>.ico</c>) без внешних инструментов:
/// скруглённый акцентный квадрат со стилизованными часами и буквой «D».
/// </summary>
/// <remarks>
/// Иконка собирается в нескольких размерах (16, 24, 32, 48, 64, 128, 256 px) и упаковывается
/// в ICO-контейнер вручную: PNG-кадры с 32-битным цветом и альфой. Используется служебным
/// режимом <c>--make-icon</c> при подготовке ресурсов проекта.
/// </remarks>
public static class IconGenerator
{
    /// <summary>Размеры кадров иконки в пикселях.</summary>
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256];

    /// <summary>Акцентный цвет иконки.</summary>
    private static readonly Color Accent = Color.FromRgb(0x4D, 0x6B, 0xFE);

    /// <summary>Тёмный цвет циферблата.</summary>
    private static readonly Color DialColor = Color.FromRgb(0x14, 0x17, 0x22);

    /// <summary>
    /// Создаёт ICO-файл с иконкой приложения.
    /// </summary>
    /// <param name="filePath">Путь к сохраняемому <c>.ico</c>.</param>
    /// <exception cref="ArgumentException">Путь пуст.</exception>
    public static void CreateIcon(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var frames = new List<(int Size, byte[] Png)>();
        foreach (int size in IconSizes)
        {
            frames.Add((size, RenderFrame(size)));
        }

        using FileStream stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: 1 = icon
        writer.Write((ushort)frames.Count);   // количество кадров

        int offset = 6 + (16 * frames.Count);

        // ICONDIRENTRY для каждого кадра
        foreach ((int size, byte[] png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));   // ширина (0 означает 256)
            writer.Write((byte)(size >= 256 ? 0 : size));   // высота
            writer.Write((byte)0);                          // число цветов палитры
            writer.Write((byte)0);                          // reserved
            writer.Write((ushort)1);                        // planes
            writer.Write((ushort)32);                       // бит на пиксель
            writer.Write(png.Length);                       // размер данных
            writer.Write(offset);                           // смещение данных
            offset += png.Length;
        }

        // Данные кадров
        foreach ((_, byte[] png) in frames)
        {
            writer.Write(png);
        }

        Log.Information("Иконка создана: {Path} ({Frames} кадров)", filePath, frames.Count);
    }

    /// <summary>
    /// Отрисовывает один кадр иконки и кодирует его в PNG.
    /// </summary>
    /// <param name="size">Размер кадра в пикселях.</param>
    /// <returns>PNG-данные кадра.</returns>
    private static byte[] RenderFrame(int size)
    {
        double scale = size / 256d;

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            double padding = 12 * scale;
            double side = size - (2 * padding);
            var rect = new Rect(padding, padding, side, side);

            // Скруглённый квадрат с акцентной заливкой.
            var background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            background.GradientStops.Add(new GradientStop(Color.FromRgb(0x6B, 0x8B, 0xFF), 0));
            background.GradientStops.Add(new GradientStop(Accent, 0.55));
            background.GradientStops.Add(new GradientStop(Color.FromRgb(0x2E, 0x45, 0xD6), 1));
            background.Freeze();

            double cornerRadius = side * 0.24;
            context.DrawRoundedRectangle(background, null, rect, cornerRadius, cornerRadius);

            // Циферблат: тёмный круг внутри.
            double dialInset = side * 0.17;
            var dialRect = new Rect(
                rect.X + dialInset,
                rect.Y + dialInset,
                side - (2 * dialInset),
                side - (2 * dialInset));

            var dialBrush = new SolidColorBrush(DialColor) { Opacity = 0.92 };
            dialBrush.Freeze();
            context.DrawEllipse(dialBrush, null, new Point(size / 2d, size / 2d), dialRect.Width / 2, dialRect.Height / 2);

            // Стрелки часов: «простой» от 10 к 2 — стилизованная пауза.
            var handPen = new Pen(Brushes.White, Math.Max(1.6, side * 0.075))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            handPen.Freeze();

            Point center = new(size / 2d, size / 2d);
            double handLength = dialRect.Width * 0.34;

            context.DrawLine(handPen, center, new Point(center.X, center.Y - handLength));
            context.DrawLine(handPen, center, new Point(center.X + (handLength * 0.72), center.Y));

            // Центральная точка.
            var dotBrush = new SolidColorBrush(Accent);
            dotBrush.Freeze();
            double dotRadius = Math.Max(1.2, side * 0.045);
            context.DrawEllipse(dotBrush, null, center, dotRadius, dotRadius);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var memory = new MemoryStream();
        encoder.Save(memory);
        return memory.ToArray();
    }
}