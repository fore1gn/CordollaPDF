using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CordollaPDF.Interop;

internal static class WindowBackdropHelper
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int DwmMicaEffect = 1029;

    public static void TryApply(Window window)
    {
        if (window is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var darkMode = 1;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref darkMode, sizeof(int));

        var cornerPreference = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));

        var build = Environment.OSVersion.Version.Build;
        var appliedBackdrop = false;

        if (build >= 22621)
        {
            var backdropType = 2; // DWMSBT_MAINWINDOW (Mica)
            appliedBackdrop = DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdropType, sizeof(int)) == 0;
        }
        else if (build >= 22000)
        {
            var micaEnabled = 1;
            appliedBackdrop = DwmSetWindowAttribute(handle, DwmMicaEffect, ref micaEnabled, sizeof(int)) == 0;
        }

        if (appliedBackdrop)
        {
            return;
        }

        TryApplyAccentBlur(handle, build);
    }

    private static void TryApplyAccentBlur(IntPtr handle, int build)
    {
        var accent = new AccentPolicy
        {
            AccentState = build >= 17134
                ? AccentState.AccentEnableAcrylicBlurBehind
                : AccentState.AccentEnableBlurBehind,
            AccentFlags = 2,
            GradientColor = unchecked((int)0x9A2E343A),
            AnimationId = 0
        };

        var accentSize = Marshal.SizeOf<AccentPolicy>();
        var accentPointer = Marshal.AllocHGlobal(accentSize);

        try
        {
            Marshal.StructureToPtr(accent, accentPointer, false);

            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WcaAccentPolicy,
                Data = accentPointer,
                SizeOfData = accentSize
            };

            _ = SetWindowCompositionAttribute(handle, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPointer);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd,
        ref WindowCompositionAttributeData data);

    private enum AccentState
    {
        AccentDisabled = 0,
        AccentEnableGradient = 1,
        AccentEnableTransparentGradient = 2,
        AccentEnableBlurBehind = 3,
        AccentEnableAcrylicBlurBehind = 4
    }

    private enum WindowCompositionAttribute
    {
        WcaAccentPolicy = 19
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }
}
