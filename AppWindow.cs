using System.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace PrintFins;

sealed class AppWindow : Form
{
    readonly string _url;

    public AppWindow(string url)
    {
        _url = url;
        Text = "Support Fins";
        WindowState = FormWindowState.Maximized;
        var view = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(view);
        Load += async (_, _) => await OpenPageAsync(view);
    }

    async Task OpenPageAsync(Microsoft.Web.WebView2.WinForms.WebView2 view)
    {
        try
        {
            var dataFolder = ResolveWebViewDataFolder();
            Directory.CreateDirectory(dataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            await view.EnsureCoreWebView2Async(environment);

            var core = view.CoreWebView2;
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

            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            var detail = ex is WebView2RuntimeNotFoundException
                ? "The in-app window needs the Microsoft Edge WebView2 Runtime, which was not found."
                    + Environment.NewLine + Environment.NewLine + ex.Message
                : ex.Message;
            MessageBox.Show(detail, "PrintFins", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
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
}
