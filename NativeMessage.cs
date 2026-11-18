using System.Runtime.InteropServices;

namespace PrintFins;

static class NativeMessage
{
    const uint MbOk = 0x00000000;
    const uint MbIconError = 0x00000010;
    const uint MbIconInformation = 0x00000040;

    public static void Error(string message) =>
        MessageBoxW(IntPtr.Zero, message, "PrintFins", MbOk | MbIconError);

    public static void Info(string title, string message) =>
        MessageBoxW(IntPtr.Zero, message, title, MbOk | MbIconInformation);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
