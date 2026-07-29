using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Soundboard.App.Hotkeys;

public enum HotkeyTargetKind
{
    Sound,
    StopSound
}

public readonly record struct HotkeyTarget(
    HotkeyTargetKind Kind,
    Guid SoundId)
{
    public static HotkeyTarget ForSound(Guid soundId)
    {
        if (soundId == Guid.Empty)
        {
            throw new ArgumentException(
                "A stable sound ID is required.",
                nameof(soundId));
        }

        return new HotkeyTarget(HotkeyTargetKind.Sound, soundId);
    }

    public static HotkeyTarget StopSound { get; } =
        new(HotkeyTargetKind.StopSound, Guid.Empty);
}

public enum HotkeyRegistrationState
{
    NotAssigned,
    Registered,
    Unavailable,
    Disabled
}

public sealed record HotkeyBindingStatus(
    HotkeyTarget Target,
    HotkeyGesture? Hotkey,
    HotkeyRegistrationState State,
    string? Error);

public sealed class HotkeyInvokedEventArgs(
    HotkeyTarget target) : EventArgs
{
    public HotkeyTarget Target { get; } = target;
}

internal interface IGlobalHotkeyRegistrar
{
    bool Register(
        nint windowHandle,
        int registrationId,
        HotkeyModifiers modifiers,
        uint virtualKey,
        out int errorCode);

    bool Unregister(
        nint windowHandle,
        int registrationId,
        out int errorCode);
}

public sealed partial class GlobalHotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int FirstRegistrationId = 0x4000;

    private readonly object sync = new();
    private readonly Dispatcher dispatcher;
    private readonly IGlobalHotkeyRegistrar registrar;
    private readonly nint windowHandle;
    private readonly HwndSource source;
    private readonly Dictionary<HotkeyTarget, Binding> bindings = [];
    private readonly Dictionary<int, HotkeyTarget> targetsByRegistrationId = [];
    private int nextRegistrationId = FirstRegistrationId;
    private bool disposed;

    public GlobalHotkeyService(
        Window window,
        bool enabled)
        : this(
            window,
            enabled,
            WindowsGlobalHotkeyRegistrar.Instance)
    {
    }

    internal GlobalHotkeyService(
        Window window,
        bool enabled,
        IGlobalHotkeyRegistrar registrar)
    {
        ArgumentNullException.ThrowIfNull(window);
        this.registrar = registrar
            ?? throw new ArgumentNullException(nameof(registrar));
        dispatcher = window.Dispatcher;
        windowHandle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException(
                "The WPF window message source is unavailable.");
        source.AddHook(WindowMessageHook);
        Enabled = enabled;
    }

    public event EventHandler<HotkeyInvokedEventArgs>? HotkeyInvoked;

    public bool Enabled { get; private set; }

    public string? LastRegistrationError { get; private set; }

    public IReadOnlyList<HotkeyBindingStatus> Statuses
    {
        get
        {
            lock (sync)
            {
                return bindings.Values
                    .Select(ToStatus)
                    .ToArray();
            }
        }
    }

    public HotkeyBindingStatus GetStatus(HotkeyTarget target)
    {
        lock (sync)
        {
            return bindings.TryGetValue(target, out var binding)
                ? ToStatus(binding)
                : new HotkeyBindingStatus(
                    target,
                    null,
                    HotkeyRegistrationState.NotAssigned,
                    null);
        }
    }

    public void LoadPersistedBinding(
        HotkeyTarget target,
        HotkeyGesture hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        VerifyAccessAndNotDisposed();

        lock (sync)
        {
            if (bindings.ContainsKey(target))
            {
                throw new InvalidOperationException(
                    "The target already has a loaded hotkey binding.");
            }

            var binding = new Binding(
                target,
                AllocateRegistrationId(),
                hotkey);
            bindings.Add(target, binding);

            var conflict = FindConflict(target, hotkey);
            if (conflict is not null)
            {
                SetUnavailable(
                    binding,
                    $"The combination {hotkey.DisplayText} is also "
                    + $"assigned to {DescribeTarget(conflict.Value)}.");
            }
            else if (Enabled)
            {
                TryRegisterCore(binding);
            }
            else
            {
                binding.State = HotkeyRegistrationState.Disabled;
            }
        }
    }

    public bool TryReplaceBinding(
        HotkeyTarget target,
        HotkeyGesture? proposed,
        out string? error)
    {
        VerifyAccessAndNotDisposed();

        lock (sync)
        {
            if (proposed is not null)
            {
                if (!HotkeyGesture.TryNormalize(
                        proposed,
                        out var normalized,
                        out error))
                {
                    return false;
                }

                proposed = normalized;
                var conflict = FindConflict(target, proposed!);
                if (conflict is not null)
                {
                    error =
                        $"{proposed!.DisplayText} is already assigned to "
                        + $"{DescribeTarget(conflict.Value)}.";
                    LastRegistrationError = error;
                    return false;
                }
            }

            if (!bindings.TryGetValue(target, out var binding))
            {
                if (proposed is null)
                {
                    error = null;
                    return true;
                }

                binding = new Binding(
                    target,
                    AllocateRegistrationId(),
                    proposed);
                bindings.Add(target, binding);

                if (!TryActivateProposed(binding, proposed, out error))
                {
                    bindings.Remove(target);
                    return false;
                }

                return true;
            }

            if (proposed is null)
            {
                UnregisterCore(binding);
                bindings.Remove(target);
                error = null;
                return true;
            }

            if (binding.Hotkey == proposed
                && (binding.State == HotkeyRegistrationState.Registered
                    || !Enabled))
            {
                error = null;
                return true;
            }

            var previousHotkey = binding.Hotkey;
            var previousWasRegistered =
                binding.State == HotkeyRegistrationState.Registered;

            if (!Probe(proposed, out error))
            {
                return false;
            }

            UnregisterCore(binding);
            binding.Hotkey = proposed;
            if (!Enabled)
            {
                binding.State = HotkeyRegistrationState.Disabled;
                binding.Error = null;
                error = null;
                return true;
            }

            if (TryRegisterCore(binding))
            {
                error = null;
                return true;
            }

            error = binding.Error;
            binding.Hotkey = previousHotkey;
            if (previousWasRegistered && TryRegisterCore(binding))
            {
                error += " The previous assignment remains registered.";
            }
            else if (previousWasRegistered)
            {
                error += " The previous assignment could not be restored: "
                    + binding.Error;
            }
            else
            {
                binding.State = HotkeyRegistrationState.Unavailable;
            }

            LastRegistrationError = error;
            return false;
        }
    }

    public void RemoveBinding(HotkeyTarget target)
    {
        VerifyAccessAndNotDisposed();
        lock (sync)
        {
            if (!bindings.Remove(target, out var binding))
            {
                return;
            }

            UnregisterCore(binding);
        }
    }

    public IReadOnlyList<HotkeyBindingStatus> SetEnabled(bool enabled)
    {
        VerifyAccessAndNotDisposed();
        lock (sync)
        {
            Enabled = enabled;
            foreach (var binding in bindings.Values)
            {
                if (!enabled)
                {
                    UnregisterCore(binding);
                    binding.State = HotkeyRegistrationState.Disabled;
                    binding.Error = null;
                }
                else
                {
                    var conflict = FindConflict(
                        binding.Target,
                        binding.Hotkey);
                    if (conflict is not null)
                    {
                        SetUnavailable(
                            binding,
                            $"The combination {binding.Hotkey.DisplayText} "
                            + $"is also assigned to "
                            + $"{DescribeTarget(conflict.Value)}.");
                    }
                    else
                    {
                        TryRegisterCore(binding);
                    }
                }
            }

            return bindings.Values.Select(ToStatus).ToArray();
        }
    }

    public IReadOnlyList<HotkeyBindingStatus> RetryUnavailable()
    {
        VerifyAccessAndNotDisposed();
        lock (sync)
        {
            if (!Enabled)
            {
                return bindings.Values.Select(ToStatus).ToArray();
            }

            foreach (var binding in bindings.Values
                         .Where(
                             item =>
                                 item.State
                                 == HotkeyRegistrationState.Unavailable))
            {
                var conflict = FindConflict(
                    binding.Target,
                    binding.Hotkey);
                if (conflict is null)
                {
                    TryRegisterCore(binding);
                }
            }

            return bindings.Values.Select(ToStatus).ToArray();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Dispose);
            return;
        }

        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (var binding in bindings.Values)
            {
                UnregisterCore(binding);
            }

            bindings.Clear();
            targetsByRegistrationId.Clear();
            HotkeyInvoked = null;
        }

        source.RemoveHook(WindowMessageHook);
        GC.SuppressFinalize(this);
    }

    private nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message != WmHotkey)
        {
            return 0;
        }

        HotkeyTarget? target = null;
        lock (sync)
        {
            if (!disposed
                && Enabled
                && targetsByRegistrationId.TryGetValue(
                    unchecked((int)wParam),
                    out var registeredTarget))
            {
                target = registeredTarget;
            }
        }

        if (target is not null)
        {
            handled = true;
            _ = dispatcher.BeginInvoke(
                DispatcherPriority.Normal,
                () =>
                {
                    if (!disposed)
                    {
                        HotkeyInvoked?.Invoke(
                            this,
                            new HotkeyInvokedEventArgs(target.Value));
                    }
                });
        }

        return 0;
    }

    private bool TryActivateProposed(
        Binding binding,
        HotkeyGesture proposed,
        out string? error)
    {
        if (!Enabled)
        {
            if (!Probe(proposed, out error))
            {
                return false;
            }

            binding.State = HotkeyRegistrationState.Disabled;
            binding.Error = null;
            return true;
        }

        if (!TryRegisterCore(binding))
        {
            error = binding.Error;
            return false;
        }

        error = null;
        return true;
    }

    private bool Probe(
        HotkeyGesture proposed,
        out string? error)
    {
        var probeId = AllocateRegistrationId();
        if (!registrar.Register(
                windowHandle,
                probeId,
                proposed.Modifiers,
                proposed.VirtualKey,
                out var errorCode))
        {
            error = BuildRegistrationError(proposed, errorCode);
            LastRegistrationError = error;
            return false;
        }

        registrar.Unregister(windowHandle, probeId, out _);
        error = null;
        return true;
    }

    private bool TryRegisterCore(Binding binding)
    {
        UnregisterCore(binding);
        if (!registrar.Register(
                windowHandle,
                binding.RegistrationId,
                binding.Hotkey.Modifiers,
                binding.Hotkey.VirtualKey,
                out var errorCode))
        {
            SetUnavailable(
                binding,
                BuildRegistrationError(binding.Hotkey, errorCode));
            return false;
        }

        binding.State = HotkeyRegistrationState.Registered;
        binding.Error = null;
        targetsByRegistrationId[binding.RegistrationId] = binding.Target;
        return true;
    }

    private void UnregisterCore(Binding binding)
    {
        if (binding.State != HotkeyRegistrationState.Registered)
        {
            targetsByRegistrationId.Remove(binding.RegistrationId);
            return;
        }

        registrar.Unregister(
            windowHandle,
            binding.RegistrationId,
            out _);
        targetsByRegistrationId.Remove(binding.RegistrationId);
        binding.State = HotkeyRegistrationState.Unavailable;
    }

    private HotkeyTarget? FindConflict(
        HotkeyTarget target,
        HotkeyGesture hotkey)
    {
        foreach (var binding in bindings.Values)
        {
            if (binding.Target != target
                && binding.Hotkey == hotkey)
            {
                return binding.Target;
            }
        }

        return null;
    }

    private void SetUnavailable(
        Binding binding,
        string error)
    {
        binding.State = HotkeyRegistrationState.Unavailable;
        binding.Error = error;
        targetsByRegistrationId.Remove(binding.RegistrationId);
        LastRegistrationError = error;
    }

    private int AllocateRegistrationId()
    {
        if (nextRegistrationId >= 0xBFFF)
        {
            throw new InvalidOperationException(
                "The application exhausted its hotkey registration IDs.");
        }

        return nextRegistrationId++;
    }

    private void VerifyAccessAndNotDisposed()
    {
        dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    private static HotkeyBindingStatus ToStatus(Binding binding)
    {
        return new HotkeyBindingStatus(
            binding.Target,
            binding.Hotkey,
            binding.State,
            binding.Error);
    }

    private static string BuildRegistrationError(
        HotkeyGesture hotkey,
        int errorCode)
    {
        var reason = errorCode == 0
            ? "Windows did not provide an error code."
            : new Win32Exception(errorCode).Message;
        return $"Windows could not register {hotkey.DisplayText}. "
            + "The combination may be reserved or owned by another "
            + $"application. {reason} (Win32 error {errorCode}).";
    }

    private static string DescribeTarget(HotkeyTarget target)
    {
        return target.Kind == HotkeyTargetKind.StopSound
            ? "Stop Sound"
            : $"sound {target.SoundId}";
    }

    private sealed class Binding(
        HotkeyTarget target,
        int registrationId,
        HotkeyGesture hotkey)
    {
        public HotkeyTarget Target { get; } = target;

        public int RegistrationId { get; } = registrationId;

        public HotkeyGesture Hotkey { get; set; } = hotkey;

        public HotkeyRegistrationState State { get; set; }

        public string? Error { get; set; }
    }

    private sealed class WindowsGlobalHotkeyRegistrar
        : IGlobalHotkeyRegistrar
    {
        public static WindowsGlobalHotkeyRegistrar Instance { get; } = new();

        public bool Register(
            nint windowHandle,
            int registrationId,
            HotkeyModifiers modifiers,
            uint virtualKey,
            out int errorCode)
        {
            var result = NativeMethods.RegisterHotKey(
                windowHandle,
                registrationId,
                (uint)modifiers | ModNoRepeat,
                virtualKey);
            errorCode = result
                ? 0
                : Marshal.GetLastPInvokeError();
            return result;
        }

        public bool Unregister(
            nint windowHandle,
            int registrationId,
            out int errorCode)
        {
            var result = NativeMethods.UnregisterHotKey(
                windowHandle,
                registrationId);
            errorCode = result
                ? 0
                : Marshal.GetLastPInvokeError();
            return result;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport(
            "user32.dll",
            EntryPoint = "RegisterHotKey",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool RegisterHotKey(
            nint windowHandle,
            int registrationId,
            uint modifiers,
            uint virtualKey);

        [LibraryImport(
            "user32.dll",
            EntryPoint = "UnregisterHotKey",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnregisterHotKey(
            nint windowHandle,
            int registrationId);
    }
}
