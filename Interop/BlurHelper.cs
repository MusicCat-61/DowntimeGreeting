using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;

namespace DowntimeGreeting.Interop;

/// <summary>
/// Вспомогательный класс для включения размытия фона за окном средствами DWM (P/Invoke).
/// </summary>
/// <remarks>
/// Используются два способа:
/// <list type="bullet">
/// <item><see cref="DwmEnableBlurBehindWindow"/> + <see cref="DwmExtendFrameIntoClientArea"/> — «стекло» DWM;</item>
/// <item><see cref="SetWindowCompositionAttribute"/> с политикой акцента (Acrylic/Blur) — доступно в Windows 10/11.</item>
/// </list>
/// Важно: если у окна включено <see cref="Window.AllowsTransparency"/> (слоистое окно WPF),
/// DWM не композитирует размытие — окно рисуется через UpdateLayeredWindow. В этом случае
/// «стеклянный» вид создаётся полупрозрачным фоном и бликом в XAML, а вызовы DWM остаются
/// безвредными и сохраняются для совместимости.
/// </remarks>
public static class BlurHelper
{
    /// <summary>Флаг <c>DWM_BB_ENABLE</c> — включить размытие.</summary>
    private const int DwmBlurBehindEnable = 0x00000001;

    /// <summary>Неограниченная область стеклянной рамки (рамка растянута на весь клиент).</summary>
    private const int FrameMarginInfinite = -1;

    /// <summary>Атрибут <c>WCA_ACCENT_POLICY</c> для <c>SetWindowCompositionAttribute</c>.</summary>
    private const int WcaAccentPolicy = 19;

    /// <summary>Состояние акцента: размытие за окном.</summary>
    private const int AccentEnableBlurBehind = 3;

    /// <summary>Состояние акцента: акриловое размытие (Windows 10 1803+).</summary>
    private const int AccentEnableAcrylicBlurBehind = 4;

    /// <summary>Прямоугольные отступы рамки для <c>DwmExtendFrameIntoClientArea</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        /// <summary>Левая граница.</summary>
        public int Left;

        /// <summary>Правая граница.</summary>
        public int Right;

        /// <summary>Верхняя граница.</summary>
        public int Top;

        /// <summary>Нижняя граница.</summary>
        public int Bottom;
    }

    /// <summary>Структура <c>DWM_BLURBEHIND</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlurBehind
    {
        /// <summary>Комбинация флагов <c>DWM_BB_*</c>.</summary>
        public int Flags;

        /// <summary>Признак включения размытия.</summary>
        public bool Enable;

        /// <summary>Область размытия; <see cref="IntPtr.Zero"/> — всё окно.</summary>
        public IntPtr Region;

        /// <summary>Признак анимации при разворачивании окна.</summary>
        public bool TransitionOnMaximized;
    }

    /// <summary>Политика акцента для <c>SetWindowCompositionAttribute</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        /// <summary>Состояние акцента (<c>ACCENT_ENABLE_*</c>).</summary>
        public int AccentState;

        /// <summary>Флаги отрисовки границ акцента.</summary>
        public int AccentFlags;

        /// <summary>Цвет тонирования в формате AABBGGRR.</summary>
        public int GradientColor;

        /// <summary>Идентификатор анимации.</summary>
        public int AnimationId;
    }

    /// <summary>Данные для <c>SetWindowCompositionAttribute</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        /// <summary>Атрибут <c>WCA_*</c>.</summary>
        public int Attribute;

        /// <summary>Указатель на данные.</summary>
        public IntPtr Data;

        /// <summary>Размер данных в байтах.</summary>
        public int SizeOfData;
    }

    /// <summary>Включает размытие фона за окном (P/Invoke <c>dwmapi!DwmEnableBlurBehindWindow</c>).</summary>
    /// <param name="hwnd">Дескриптор окна.</param>
    /// <param name="blurBehind">Описание параметров размытия.</param>
    /// <returns>Код результата HRESULT (0 — успех).</returns>
    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref BlurBehind blurBehind);

    /// <summary>Растягивает стеклянную рамку DWM на весь клиент окна.</summary>
    /// <param name="hwnd">Дескриптор окна.</param>
    /// <param name="margins">Отступы рамки.</param>
    /// <returns>Код результата HRESULT.</returns>
    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    /// <summary>Задаёт атрибуты композиции окна (внутренний API DWM).</summary>
    /// <param name="hwnd">Дескриптор окна.</param>
    /// <param name="data">Описание атрибута.</param>
    /// <returns><see langword="true"/> при успехе.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Пытается включить размытие фона за указанным окном.
    /// </summary>
    /// <param name="window">Окно WPF.</param>
    /// <returns><see langword="true"/>, если размытие удалось включить.</returns>
    public static bool TryApplyBlur(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Log.Debug("Дескриптор окна ещё не создан — размытие не включено");
            return false;
        }

        bool dwmBlurApplied = TryEnableDwmBlurBehind(hwnd);
        bool accentApplied = TryEnableAccentPolicy(hwnd, window.AllowsTransparency);

        Log.Debug(
            "Размытие: DWM={DwmBlur}, Accent={Accent}, AllowsTransparency={AllowsTransparency}",
            dwmBlurApplied,
            accentApplied,
            window.AllowsTransparency);

        if (window.AllowsTransparency && accentApplied)
        {
            // Слоистое окно не может показать размытие DWM: визуально стекло создаётся
            // полупрозрачным фоном и градиентным бликом. Это ожидаемое поведение.
            Log.Debug("Окно слоистое (AllowsTransparency=True): размытие DWM не будет отрисовано");
        }

        return dwmBlurApplied || accentApplied;
    }

    /// <summary>Атрибут DWM: предпочтение по скруглению углов окна.</summary>
    private const int DwmWindowCornerPreference = 33;

    /// <summary>Значение атрибута: скруглять углы.</summary>
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>
    /// Включает скругление углов окна средствами DWM (Windows 11).
    /// </summary>
    /// <param name="window">Окно приложения.</param>
    /// <returns>
    /// <see langword="true"/>, если система применила скругление; на Windows 10 вызов
    /// возвращает <see langword="false"/>, и окно остаётся с прямыми углами.
    /// </returns>
    /// <remarks>
    /// Слоистое окно (<see cref="Window.AllowsTransparency"/> = <see langword="true"/>) скругляется
    /// собственной маской в XAML, но размер такого окна изменить нельзя. Поэтому главное окно
    /// непрозрачное, а форму ему задаёт DWM.
    /// </remarks>
    public static bool TryApplyRoundedCorners(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            int preference = DwmWindowCornerRound;
            int result = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref preference, sizeof(int));

            if (result != 0)
            {
                Log.Debug("Скругление углов недоступно (код {Code}) — вероятно, Windows 10", result);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug("DWM не поддерживает скругление углов");
            return false;
        }
    }

    /// <summary>
    /// Включает размытие через DWM и растягивает стеклянную рамку на весь клиент.
    /// </summary>
    /// <param name="hwnd">Дескриптор окна.</param>
    /// <returns><see langword="true"/> при успехе.</returns>
    private static bool TryEnableDwmBlurBehind(IntPtr hwnd)
    {
        try
        {
            var margins = new Margins
            {
                Left = FrameMarginInfinite,
                Right = FrameMarginInfinite,
                Top = FrameMarginInfinite,
                Bottom = FrameMarginInfinite
            };

            int extendResult = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (extendResult != 0)
            {
                Log.Debug("DwmExtendFrameIntoClientArea вернул {Result}", extendResult);
            }

            var blurBehind = new BlurBehind
            {
                Flags = DwmBlurBehindEnable,
                Enable = true,
                Region = IntPtr.Zero,
                TransitionOnMaximized = false
            };

            int blurResult = DwmEnableBlurBehindWindow(hwnd, ref blurBehind);
            if (blurResult != 0)
            {
                Log.Debug("DwmEnableBlurBehindWindow вернул {Result}", blurResult);
                return false;
            }

            return true;
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning(ex, "dwmapi.dll недоступна — размытие DWM отключено");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.Warning(ex, "Функция размытия DWM недоступна в этой версии Windows");
            return false;
        }
    }

    /// <summary>
    /// Включает акриловое/обычное размытие через <c>SetWindowCompositionAttribute</c>.
    /// </summary>
    /// <param name="hwnd">Дескриптор окна.</param>
    /// <param name="isLayeredWindow">Признак слоистого окна WPF.</param>
    /// <returns><see langword="true"/> при успехе.</returns>
    private static bool TryEnableAccentPolicy(IntPtr hwnd, bool isLayeredWindow)
    {
        if (isLayeredWindow)
        {
            // Для слоистого окна политика акцента не применяется — не тратим вызовы.
            return false;
        }

        IntPtr policyPointer = IntPtr.Zero;
        try
        {
            var policy = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 0,
                GradientColor = 0x00000000,
                AnimationId = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            policyPointer = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(policy, policyPointer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                SizeOfData = size,
                Data = policyPointer
            };

            if (SetWindowCompositionAttribute(hwnd, ref data))
            {
                return true;
            }

            // Windows 10 без поддержки акрила — пробуем простое размытие.
            policy.AccentState = AccentEnableBlurBehind;
            Marshal.StructureToPtr(policy, policyPointer, false);
            return SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (DllNotFoundException ex)
        {
            Log.Warning(ex, "SetWindowCompositionAttribute недоступна");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            Log.Warning(ex, "SetWindowCompositionAttribute недоступна в этой версии Windows");
            return false;
        }
        finally
        {
            if (policyPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(policyPointer);
            }
        }
    }
}
