using System.Runtime.InteropServices;

/// <summary>Windows ends every timer wait on its clock tick, 15.6 ms unless a process asks for a finer one, so the
/// Gateway's 33 ms frames went out 31 or 47 ms apart and a lockstep frame end up to a tick late (07 §6). While a player is
/// in game the Gateway asks for 1 ms. Every launcher starts it without a window, and Windows 11 ignores a hidden process's
/// request unless the process opts out of that power throttling, so it does that too. Elsewhere nothing changes.</summary>
public static class WindowsTimerResolution
{
    // Held per connection in game (InGame); the request and the opt-out stand while any is held. Windows drops both when
    // the process ends, so a killed Gateway leaves nothing behind.
    static readonly object Gate = new();
    static int holders;
    static bool told;

    public static void Acquire()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (holders++ > 0) return;
            // Windows 10 has no such throttling and refuses the flag; it honours the request without it.
            bool optedOut = Throttle(ignoreTimerResolution: false);
            int error = Marshal.GetLastWin32Error();
            bool granted = timeBeginPeriod(1) == 0;
            if ((!optedOut || !granted) && !told)
            {
                told = true;
                Console.WriteLine($"[gateway] timer resolution: {(granted ? "1 ms" : "1 ms refused")}, " +
                    $"{(optedOut ? "power throttling opt-out" : $"power throttling opt-out refused (error {error})")}");
            }
        }
    }

    public static void Release()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (holders == 0 || --holders > 0) return;
            timeEndPeriod(1);
            Throttle(ignoreTimerResolution: null);
        }
    }

    // ignoreTimerResolution: false opts out, null hands the choice back to Windows (the Gateway never sets it otherwise).
    static bool Throttle(bool? ignoreTimerResolution)
    {
        var state = new PowerThrottlingState
        {
            Version = 1,
            ControlMask = ignoreTimerResolution is null ? 0u : IgnoreTimerResolution,
            StateMask = ignoreTimerResolution is true ? IgnoreTimerResolution : 0u,
        };
        return SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>());
    }

    const int ProcessPowerThrottling = 4;
    const uint IgnoreTimerResolution = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottlingState
    {
        public uint Version, ControlMask, StateMask;
    }

    [DllImport("winmm")] static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm")] static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32", SetLastError = true)] static extern bool SetProcessInformation(nint process, int kind, ref PowerThrottlingState state, uint size);
    [DllImport("kernel32")] static extern nint GetCurrentProcess();
}
