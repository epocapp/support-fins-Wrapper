using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace PrintFins;

static class AppWindow
{
    const int CwUseDefault = unchecked((int)0x80000000);
    const int SwMaximize = 3;
    const int WmDestroy = 0x0002;
    const int WmSize = 0x0005;
    const int WmClose = 0x0010;
    const int CsHRedraw = 0x0002;
    const int CsVRedraw = 0x0001;
    const int WsOverlappedWindow = 0x00CF0000;
    const int ColorWindow = 5;
    const int IdiApplication = 32512;
    const int IdcArrow = 32512;
    const uint PmRemove = 0x0001;
    const uint WaitObject0 = 0;
    const uint WaitTimeout = 0x00000102;
    const uint QsAllInput = 0x04FF;
    const uint MwmoInputAvailable = 0x0004;

    static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = (IntPtr)(-4);

    static CoreWebView2Controller? _controller;
    static Action? _closed;
    static bool _classRegistered;
    static readonly WndProcThunk Thunk = WndProc;
    static readonly IntPtr ThunkPtr = Marshal.GetFunctionPointerForDelegate(Thunk);

    public static void Run(string url, Action onClosed)
    {
        _closed = onClosed;
        SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

        var instance = GetModuleHandle(null);
        const string className = "PrintFinsWebViewHost";
        if (!_classRegistered)
        {
            var wc = new WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
                style = CsHRedraw | CsVRedraw,
                lpfnWndProc = ThunkPtr,
                hInstance = instance,
                hIcon = LoadIcon(IntPtr.Zero, (IntPtr)IdiApplication),
                hCursor = LoadCursor(IntPtr.Zero, (IntPtr)IdcArrow),
                hbrBackground = (IntPtr)(ColorWindow + 1),
                lpszClassName = className,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()}).");
            _classRegistered = true;
        }

        var hwnd = CreateWindowEx(
            0,
            className,
            "Support Fins",
            WsOverlappedWindow,
            CwUseDefault,
            CwUseDefault,
            CwUseDefault,
            CwUseDefault,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()}).");

        ShowWindow(hwnd, SwMaximize);
        UpdateWindow(hwnd);

        try
        {
            InitializeWebView(hwnd, url);
        }
        catch (Exception ex)
        {
            var detail = FormatWebViewError(ex);
            try
            {
                File.WriteAllText(
                    Path.Combine(AppSettings.ApplicationDirectory, "error.txt"),
                    detail);
            }
            catch { }
            NativeMessage.Error(detail);
            DestroyWindow(hwnd);
        }

        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        _controller = null;
        _closed = null;
    }

    public static void RequestClose(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    static string FormatWebViewError(Exception ex)
    {
        if (ex is WebView2RuntimeNotFoundException)
        {
            return "The in-app window needs the Microsoft Edge WebView2 Runtime, which was not found."
                + Environment.NewLine + Environment.NewLine
                + ex;
        }

        var detail = ex.ToString();
        if (string.IsNullOrWhiteSpace(detail))
            detail = ex.GetType().FullName ?? "Unknown WebView2 error.";
        if (detail.Length > 2500)
            detail = detail[..2500] + Environment.NewLine + "…";
        return detail;
    }

    static void InitializeWebView(IntPtr hwnd, string url)
    {
        var dataFolder = ResolveWebViewDataFolder();
        Directory.CreateDirectory(dataFolder);

        var environment = WaitForUiTask(CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder));
        var controller = WaitForUiTask(environment.CreateCoreWebView2ControllerAsync(hwnd));
        _controller = controller;
        controller.IsVisible = true;
        FitController(hwnd);

        var core = controller.CoreWebView2;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsReputationCheckingRequired = false;

        // Only this computer's loopback server. No internet and no LAN.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (IsAllowedRequest(e.Request.Uri))
                return;

            e.Response = core.Environment.CreateWebResourceResponse(
                null, 403, "Blocked", "Content-Type: text/plain");
        };

        core.NavigationStarting += (_, e) =>
        {
            if (IsAllowedRequest(e.Uri))
                return;

            e.Cancel = true;
            if (e.IsUserInitiated && IsExternalBrowserUrl(e.Uri))
                OpenInSystemBrowser(e.Uri);
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            // Keep iframes on the local site only. Do not hand frame loads to the system browser.
            if (!IsAllowedRequest(e.Uri))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (IsAllowedRequest(e.Uri))
                core.Navigate(e.Uri);
            else if (IsExternalBrowserUrl(e.Uri))
                OpenInSystemBrowser(e.Uri);
        };
        core.DownloadStarting += (_, e) =>
        {
            if (!IsAllowedRequest(e.DownloadOperation.Uri))
            {
                e.Cancel = true;
                e.Handled = true;
            }
        };

        core.ServerCertificateErrorDetected += (_, e) =>
        {
            e.Action = IsLocalHost(e.RequestUri)
                ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
                : CoreWebView2ServerCertificateErrorAction.Cancel;
        };
        core.NavigationCompleted += (_, _) => Program.KillConsoles();

        core.Navigate(url);
    }

    static T WaitForUiTask<T>(Task<T> task)
    {
        var handle = ((IAsyncResult)task).AsyncWaitHandle;
        while (!task.IsCompleted)
            WaitAndPump(handle);
        return task.GetAwaiter().GetResult();
    }

    static void WaitAndPump(WaitHandle handle)
    {
        while (true)
        {
            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                if (msg.message == 0x0012) // WM_QUIT
                {
                    PostQuitMessage((int)msg.wParam);
                    return;
                }

                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            var result = MsgWaitForMultipleObjects(1, [handle.SafeWaitHandle.DangerousGetHandle()], false, 50, QsAllInput);
            if (result == WaitObject0 || handle.WaitOne(0))
                return;
            if (result != WaitTimeout && result != WaitObject0 + 1)
                return;
        }
    }

    static void FitController(IntPtr hwnd)
    {
        if (_controller is null)
            return;
        GetClientRect(hwnd, out var rect);
        _controller.Bounds = new Rectangle(0, 0, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmSize:
                FitController(hwnd);
                break;
            case WmDestroy:
                try { _controller?.Close(); } catch { }
                _controller = null;
                try { _closed?.Invoke(); } catch { }
                PostQuitMessage(0);
                break;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    static bool IsAllowedRequest(string requestUri)
    {
        if (string.IsNullOrWhiteSpace(requestUri))
            return false;

        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase))
        {
            return IsLoopbackHost(uri.Host);
        }

        return false;
    }

    static bool IsExternalBrowserUrl(string requestUri)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals("tel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            && !IsLoopbackHost(uri.Host);
    }

    static void OpenInSystemBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    static bool IsLocalHost(string requestUri) =>
        Uri.TryCreate(requestUri, UriKind.Absolute, out var uri) && IsLoopbackHost(uri.Host);

    static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "[::1]", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
        || IPAddressLooksLoopback(host);

    static bool IPAddressLooksLoopback(string host) =>
        System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip);

    static string ResolveWebViewDataFolder()
    {
        var besideExe = Path.Combine(AppSettings.ApplicationDirectory, "WebView2");
        if (CanWriteDirectory(besideExe))
            return besideExe;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PrintFins",
            "WebView2");
    }

    static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    delegate IntPtr WndProcThunk(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClassEx
    {
        public uint cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WndClassEx lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(
        int dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool UpdateWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint nCount, IntPtr[] pHandles, bool bWaitAll, uint dwMilliseconds, uint dwWakeMask);

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
