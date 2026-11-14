using System.ComponentModel;
using System.Runtime.InteropServices;
using Serilog;

namespace DowntimeGreeting.Interop;

/// <summary>
/// Глобальный низкоуровневый хук клавиатуры (<c>WH_KEYBOARD_LL</c>), позволяющий обработать
/// нажатие <c>Esc</c> без активации окна приложения (плашка не должна забирать фокус).
/// </summary>
/// <remarks>
/// Хук снимается в <see cref="Dispose"/>. Событие <see cref="EscapePressed"/> возникает
/// в потоке, установившем хук (поток UI с насосом сообщений); обработчик должен быть
/// коротким или переадресовывать работу через диспетчер.
/// </remarks>
public sealed class KeyboardHook : IDisposable
{
    /// <summary>Идентификатор хука <c>WH_KEYBOARD_LL</c>.</summary>
    private const int WhKeyboardLl = 13;

    /// <summary>Сообщение <c>WM_KEYDOWN</c>.</summary>
    private const int WmKeyDown = 0x0100;

    /// <summary>Сообщение <c>WM_SYSKEYDOWN</c>.</summary>
    private const int WmSysKeyDown = 0x0104;

    /// <summary>Виртуальный код клавиши <c>Esc</c>.</summary>
    private const int VkEscape = 0x1B;

    private readonly LowLevelKeyboardProc _hookProcedure;
    private IntPtr _hookHandle;
    private bool _disposed;

    /// <summary>
    /// Инициализирует новый экземпляр <see cref="KeyboardHook"/>.
    /// </summary>
    public KeyboardHook()
    {
        // Делегат должен жить столько же, сколько хук, иначе сборщик мусора
        // освободит его и обратный вызов из native-кода приведёт к падению.
        _hookProcedure = HookCallback;
    }

    /// <summary>Событие нажатия клавиши <c>Esc</c>.</summary>
    public event EventHandler? EscapePressed;

    /// <summary>Признак того, что хук установлен.</summary>
    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>
    /// Описание низкоуровневого хука клавиатуры.
    /// </summary>
    /// <param name="nCode">Код хука; отрицательные значения означают, что обрабатывать нечего.</param>
    /// <param name="wParam">Идентификатор сообщения клавиатуры.</param>
    /// <param name="lParam">Указатель на структуру <c>KBDLLHOOKSTRUCT</c>.</param>
    /// <returns>Результат обработки для цепочки хуков.</returns>
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Структура <c>KBDLLHOOKSTRUCT</c> с информацией о нажатой клавише.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        /// <summary>Виртуальный код клавиши.</summary>
        public uint VirtualKeyCode;

        /// <summary>Аппаратный скан-код.</summary>
        public uint ScanCode;

        /// <summary>Флаги события.</summary>
        public uint Flags;

        /// <summary>Время события.</summary>
        public uint Time;

        /// <summary>Дополнительная информация.</summary>
        public UIntPtr ExtraInfo;
    }

    /// <summary>Устанавливает хук клавиатуры.</summary>
    /// <returns><see langword="true"/>, если хук установлен.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    /// <summary>Снимает установленный хук.</summary>
    /// <param name="hhk">Дескриптор хука.</param>
    /// <returns><see langword="true"/> при успехе.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>Передаёт событие следующему обработчику в цепочке хуков.</summary>
    /// <param name="hhk">Дескриптор хука.</param>
    /// <param name="nCode">Код хука.</param>
    /// <param name="wParam">Идентификатор сообщения.</param>
    /// <param name="lParam">Указатель на данные сообщения.</param>
    /// <returns>Результат следующего обработчика.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Возвращает дескриптор загруженного модуля.</summary>
    /// <param name="lpModuleName">Имя модуля; <see langword="null"/> — модуль текущего процесса.</param>
    /// <returns>Дескриптор модуля.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Устанавливает глобальный хук клавиатуры.
    /// </summary>
    /// <returns><see langword="true"/>, если хук установлен.</returns>
    public bool TryInstall()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsInstalled)
        {
            return true;
        }

        try
        {
            IntPtr moduleHandle = GetModuleHandle(null);
            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProcedure, moduleHandle, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Log.Warning(
                    "Не удалось установить хук клавиатуры: {Message} (код {Code}). Закрытие по Esc недоступно, клик по плашке работает",
                    new Win32Exception(error).Message,
                    error);
                return false;
            }

            Log.Debug("Хук клавиатуры установлен");
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warning(ex, "Низкоуровневый хук клавиатуры недоступен");
            return false;
        }
    }

    /// <summary>
    /// Обратный вызов хука клавиатуры.
    /// </summary>
    /// <param name="nCode">Код хука.</param>
    /// <param name="wParam">Идентификатор сообщения.</param>
    /// <param name="lParam">Указатель на структуру <c>KBDLLHOOKSTRUCT</c>.</param>
    /// <returns>Результат для цепочки хуков.</returns>
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
        {
            try
            {
                KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                if (data.VirtualKeyCode == VkEscape)
                {
                    // Синтетические нажатия (автоматизация, скрипты) тоже принимаются:
                    // раннее закрытие плашки не является критичной операцией.
                    EscapePressed?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (Exception ex)
            {
                // Исключения не должны выходить в unmanaged-код.
                Log.Error(ex, "Ошибка в обработчике хука клавиатуры");
            }
        }

        // Событие не подавляем: Esc продолжает доходить до активного приложения.
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>
    /// Снимает хук клавиатуры и освобождает ресурсы.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_hookHandle != IntPtr.Zero)
        {
            if (!UnhookWindowsHookEx(_hookHandle))
            {
                Log.Warning("Не удалось снять хук клавиатуры: код {Code}", Marshal.GetLastWin32Error());
            }

            _hookHandle = IntPtr.Zero;
        }

        _disposed = true;
    }
}