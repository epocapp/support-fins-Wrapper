using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrintFins;

static class Program
{
    const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FreeConsole();

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint WmClose = 0x0010;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [STAThread]
    static int Main()
    {
        try
        {
            var settings = AppSettings.Load();
            if (settings.IsBrowser)
                EnsureBrowserConsole();

            return Run(settings);
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine(ex.Message); } catch { }
            ShowStartupFailure(ex);
            return 1;
        }
    }

    static void EnsureBrowserConsole()
    {
        if (!AttachConsole(AttachParentProcess))
            AllocConsole();

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
        Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
    }

    internal static void KillConsoles()
    {
        // Detach first so closing the window does not exit this process.
        FreeConsole();
        var ours = (uint)Environment.ProcessId;
        EnumWindowsProc callback = (hwnd, _) =>
        {
            var className = new StringBuilder(64);
            if (GetClassName(hwnd, className, className.Capacity) == 0
                || className.ToString() != "ConsoleWindowClass")
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            var belongsToUs = pid == ours
                || IsChildProcess(pid, ours)
                || title.ToString().Contains("PrintFins", StringComparison.OrdinalIgnoreCase);
            if (!belongsToUs || pid == 0)
                return true;

            PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            if (pid != ours)
            {
                try
                {
                    var process = Process.GetProcessById((int)pid);
                    var name = process.ProcessName;
                    if (name.Equals("conhost", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("git", StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
                {
                }
            }

            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
    }

    static bool IsChildProcess(uint pid, uint ancestor)
    {
        for (var i = 0; i < 6 && pid != 0; i++)
        {
            var parent = ParentProcessId(pid);
            if (parent == ancestor)
                return true;
            if (parent == pid)
                return false;
            pid = parent;
        }

        return false;
    }

    static uint ParentProcessId(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return 0;
        try
        {
            var info = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
            return status == 0 ? (uint)info.InheritedFromUniqueProcessId.ToInt64() : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static void ShowStartupFailure(Exception ex)
    {
        try
        {
            EnsureWinForms();
            MessageBox.Show(
                ex.Message,
                "PrintFins",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Dialog could not be shown.
        }
    }

    static int Run(AppSettings settings)
    {
        if (settings.IsWebView)
            EnsureWinForms();
        StartupProgress.Open(settings.IsBrowser);
        try
        {
            var updates = SyncRepository(settings);

            if (!Directory.Exists(settings.ResolvedWebRoot))
                throw new InvalidDataException($"Web root was not found after clone: {settings.ResolvedWebRoot}");

            var app = BuildServer(settings);
            if (settings.IsBrowser)
                return RunBrowser(app, settings, SiteUrl(settings, https: false), updates);

            return RunWebView(app, settings, SiteUrl(settings, https: true), updates);
        }
        catch
        {
            StartupProgress.CloseForm();
            throw;
        }
    }

    static WebApplication BuildServer(AppSettings settings)
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "");
        Environment.SetEnvironmentVariable("ASPNETCORE_HTTPS_PORT", null);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.Logging.ClearProviders();
        builder.WebHost.PreferHostingUrls(false);
        builder.WebHost.ConfigureKestrel(options =>
        {
            var address = BindAddress(settings.Host);
            options.Listen(address, settings.Port);
            options.Listen(address, settings.HttpsPort, listen => listen.UseHttps());
        });

        var app = builder.Build();
        var files = new PhysicalFileProvider(settings.ResolvedWebRoot);
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".js"] = "text/javascript";
        types.Mappings[".mjs"] = "text/javascript";
        types.Mappings[".wasm"] = "application/wasm";

        app.Use(async (context, next) =>
        {
            var path = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
            var method = context.Request.Method;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.CacheControl = "no-store, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                context.Response.Headers.Expires = "0";
                return Task.CompletedTask;
            });
            await next();
            Console.WriteLine($"{method} {path} -> {context.Response.StatusCode}");
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,
            ContentTypeProvider = types,
        });

        return app;
    }

    static int RunBrowser(WebApplication app, AppSettings settings, string siteUrl, List<string> updates)
    {
        var starting = app.StartAsync();
        while (!starting.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        starting.GetAwaiter().GetResult();
        FinishStartup(settings, siteUrl, updates, "Press Ctrl+C to stop.");
        OpenSystemBrowser(siteUrl);
        app.WaitForShutdownAsync().GetAwaiter().GetResult();
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    static int RunWebView(WebApplication app, AppSettings settings, string siteUrl, List<string> updates)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        var serverTask = Task.Run(() => app.RunAsync());

        while (!started.Task.IsCompleted && !serverTask.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
        if (serverTask.IsCompleted && !started.Task.IsCompleted)
        {
            StartupProgress.CloseForm();
            serverTask.GetAwaiter().GetResult();
            return 1;
        }

        FinishStartup(settings, siteUrl, updates, "Opening a maximized window. Close it to stop.");

        EnsureWinForms();

        AppWindow? window = null;
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (window is { IsHandleCreated: true, IsDisposed: false })
                window.BeginInvoke(window.Close);
        });

        window = new AppWindow(siteUrl);
        window.FormClosed += (_, _) => app.Lifetime.StopApplication();
        Application.Run(window);

        try
        {
            serverTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    static void FinishStartup(AppSettings settings, string siteUrl, List<string> updates, string lastLine)
    {
        PrintServing(settings, siteUrl);
        StartupProgress.Line(lastLine);
        StartupProgress.CloseForm();
        if (updates.Count > 0)
            ShowUpdates(updates);
    }

    static bool _winFormsReady;

    static void EnsureWinForms()
    {
        if (_winFormsReady)
            return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _winFormsReady = true;
    }

    static void ShowUpdates(IReadOnlyList<string> titles)
    {
        EnsureWinForms();
        using var font = new Font("Segoe UI", 10f);
        using var form = new Form
        {
            Text = "Support Fins updated",
            StartPosition = FormStartPosition.CenterScreen,
            Width = 640,
            Height = 420,
            MinimumSize = new Size(420, 240),
            TopMost = true,
        };
        var close = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Dock = DockStyle.Bottom,
            Height = 36,
        };
        var list = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = font,
            Text = string.Join(Environment.NewLine, titles),
            TabStop = false,
        };
        form.Controls.Add(close);
        form.Controls.Add(list);
        form.AcceptButton = close;
        form.CancelButton = close;
        form.Shown += (_, _) =>
        {
            list.SelectionStart = 0;
            list.SelectionLength = 0;
        };
        form.ShowDialog();
    }

    static List<string> SyncRepository(AppSettings settings)
    {
        var destination = settings.ResolvedCloneDirectory;
        if (File.Exists(destination))
            throw new IOException($"Clone path is a file, not a folder: {destination}");

        if (Directory.Exists(destination))
        {
            var updates = TryUpdate(settings, destination);
            if (updates is not null)
                return updates;

            StartupProgress.Line($"Replacing {destination}");
            DeleteDirectory(destination);
        }

        var parent = Directory.GetParent(destination)?.FullName;
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        StartupProgress.Line($"Cloning {settings.RepositoryUrl}");
        var cloneExit = RunGit(null, "clone", "--depth", "1", settings.RepositoryUrl, destination);
        if (cloneExit != 0)
            throw new InvalidDataException($"git clone failed with exit code {cloneExit}.");
        ScanDownload(settings, destination);
        return [];
    }

    static List<string>? TryUpdate(AppSettings settings, string destination)
    {
        var head = Git(destination, "rev-parse", "HEAD");
        var origin = Git(destination, "remote", "get-url", "origin");
        var localCommit = head.Output.Trim();
        if (head.ExitCode != 0 || origin.ExitCode != 0 || !IsCommitId(localCommit))
            return null;

        if (!SameRepository(origin.Output, settings.RepositoryUrl))
        {
            StartupProgress.Line("Local clone is for a different repository. Replacing it.");
            return null;
        }

        StartupProgress.Line($"Local commit {localCommit}. Checking {settings.RepositoryUrl}");
        var remote = Git(null, "ls-remote", settings.RepositoryUrl, "HEAD");
        if (remote.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(remote.Error))
                StartupProgress.Line(remote.Error.TrimEnd());
            StartupProgress.Line("Could not reach the remote. Using the local clone.");
            return [];
        }

        var remoteCommit = RemoteHead(remote.Output);
        if (remoteCommit is null)
            throw new InvalidDataException("git ls-remote did not return a commit for HEAD.");

        if (string.Equals(localCommit, remoteCommit, StringComparison.OrdinalIgnoreCase))
        {
            StartupProgress.Line($"Already up to date at {localCommit}. Skipping download.");
            return [];
        }

        StartupProgress.Line($"Remote commit {remoteCommit} differs. Updating.");
        var fetchExit = RunGit(destination, "fetch", "origin");
        if (fetchExit != 0)
            throw new InvalidDataException($"git fetch failed with exit code {fetchExit}.");

        var fetched = Git(destination, "rev-parse", "FETCH_HEAD");
        var newCommit = fetched.Output.Trim();
        if (fetched.ExitCode != 0 || !IsCommitId(newCommit))
            throw new InvalidDataException("git fetch did not report a commit.");

        var titles = CommitTitles(destination, localCommit, newCommit);
        var reset = Git(destination, "reset", "--hard", newCommit);
        if (reset.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(reset.Error)
                ? $"git reset failed with exit code {reset.ExitCode}."
                : reset.Error.Trim();
            throw new InvalidDataException(detail);
        }

        ScanDownload(settings, destination);
        return titles;
    }

    static void ScanDownload(AppSettings settings, string directory)
    {
        if (!settings.ScanDownloads)
            return;

        string fileName;
        List<string> args;
        try
        {
            if (string.IsNullOrWhiteSpace(settings.AntivirusCommand))
            {
                fileName = FindDefender();
                args = ["-Scan", "-ScanType", "3", "-File", directory];
            }
            else
            {
                var command = settings.AntivirusCommand.Replace("{path}", directory, StringComparison.OrdinalIgnoreCase);
                (fileName, args) = SplitCommand(command);
            }
        }
        catch (InvalidDataException)
        {
            DeleteFailedDownload(directory);
            throw;
        }

        StartupProgress.Line($"Scanning {directory} with {Path.GetFileName(fileName)}");

        Process process;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            process = Process.Start(start)
                ?? throw new InvalidDataException($"Antivirus could not be started: {fileName}");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidDataException)
        {
            DeleteFailedDownload(directory);
            if (ex is InvalidDataException)
                throw;
            throw new InvalidDataException($"Antivirus could not be started: {fileName}");
        }

        using (process)
        {
            var output = new StringBuilder();
            process.OutputDataReceived += (_, e) => AppendScanLine(output, e.Data);
            process.ErrorDataReceived += (_, e) => AppendScanLine(output, e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            while (!process.WaitForExit(50))
                Application.DoEvents();
            process.WaitForExit();
            if (process.ExitCode == 0)
            {
                StartupProgress.Line("Scan finished with no threats.");
                return;
            }

            DeleteFailedDownload(directory);
            var detail = output.Length == 0
                ? $"exit code {process.ExitCode}"
                : output.ToString().Trim();
            throw new InvalidDataException(
                $"Antivirus scan failed ({process.ExitCode}). The download was deleted.{Environment.NewLine}{detail}");
        }
    }

    static void AppendScanLine(StringBuilder output, string? line)
    {
        if (line is null)
            return;
        StartupProgress.Line(line);
        output.AppendLine(line);
    }

    static string FindDefender()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows Defender", "Platform");
        if (!Directory.Exists(root))
            throw new InvalidDataException("Windows Defender could not be started. MpCmdRun.exe was not found.");

        string? bestPath = null;
        Version? bestVersion = null;
        foreach (var directory in Directory.GetDirectories(root))
        {
            var exe = Path.Combine(directory, "MpCmdRun.exe");
            if (!File.Exists(exe))
                continue;
            var version = DefenderVersion(directory);
            if (bestPath is null || (version is not null && (bestVersion is null || version > bestVersion)))
            {
                bestPath = exe;
                bestVersion = version;
            }
        }

        return bestPath
            ?? throw new InvalidDataException("Windows Defender could not be started. MpCmdRun.exe was not found.");
    }

    static Version? DefenderVersion(string directory)
    {
        var name = Path.GetFileName(directory);
        var dash = name.IndexOf('-');
        var text = dash > 0 ? name[..dash] : name;
        return Version.TryParse(text, out var version) ? version : null;
    }

    static (string FileName, List<string> Args) SplitCommand(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var character in command)
        {
            if (character == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(character);
        }

        if (inQuotes)
            throw new InvalidDataException("settings.json: antivirusCommand has an unmatched quote.");
        if (current.Length > 0)
            tokens.Add(current.ToString());
        if (tokens.Count == 0)
            throw new InvalidDataException("settings.json: antivirusCommand is empty.");

        return (tokens[0], tokens.Skip(1).ToList());
    }

    static void DeleteFailedDownload(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                DeleteDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                $"Antivirus scan failed and the download could not be deleted ({directory}). {ex.Message}");
        }
    }

    static List<string> CommitTitles(string destination, string oldCommit, string newCommit)
    {
        var ancestor = Git(destination, "merge-base", "--is-ancestor", oldCommit, newCommit);
        if (ancestor.ExitCode == 0)
        {
            var log = Git(destination, "-c", "i18n.logOutputEncoding=utf-8", "--no-pager", "log", "--pretty=format:%s", $"{oldCommit}..{newCommit}");
            if (log.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(log.Error) ? "git log failed." : log.Error.Trim();
                throw new InvalidDataException(detail);
            }

            var titles = log.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            if (titles.Count > 0)
                return titles;
        }

        var tip = Git(destination, "-c", "i18n.logOutputEncoding=utf-8", "--no-pager", "log", "-1", "--pretty=format:%s", newCommit);
        var title = tip.Output.Trim();
        return title.Length == 0 ? [] : [title];
    }

    static bool SameRepository(string left, string right) =>
        string.Equals(NormalizeRepositoryUrl(left), NormalizeRepositoryUrl(right), StringComparison.OrdinalIgnoreCase);

    static string NormalizeRepositoryUrl(string url)
    {
        var value = url.Trim().TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        return value;
    }

    static bool IsCommitId(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    static string? RemoteHead(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1] == "HEAD" && IsCommitId(parts[0]))
                return parts[0];
        }

        return null;
    }

    readonly record struct GitResult(int ExitCode, string Output, string Error);

    static int RunGit(string? workingDirectory, params string[] args) =>
        Git(workingDirectory, args).ExitCode;

    static GitResult Git(string? workingDirectory, params string[] args)
    {
        using var process = StartGit(workingDirectory, args);
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendGitText(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendGitText(error, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        while (!process.WaitForExit(50))
            Application.DoEvents();
        process.WaitForExit();
        return new GitResult(process.ExitCode, output.ToString(), error.ToString());
    }

    static void AppendGitText(StringBuilder buffer, string? line)
    {
        if (line is null)
            return;
        buffer.AppendLine(line);
        StartupProgress.Line(line);
    }

    static Process StartGit(string? workingDirectory, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrEmpty(workingDirectory))
                start.WorkingDirectory = workingDirectory;
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            return Process.Start(start)
                ?? throw new InvalidDataException("Could not start git.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new InvalidDataException("git was not found on PATH. Install Git and try again.");
        }
    }

    static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    static void OpenSystemBrowser(string url)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }

    static void PrintServing(AppSettings settings, string siteUrl)
    {
        StartupProgress.Line($"support-fins: {siteUrl} (serving {settings.ResolvedWebRoot})");
        StartupProgress.Line($"Listening on HTTP {settings.Port} and HTTPS {settings.HttpsPort}. Opening {siteUrl}");
        if (settings.Host is "0.0.0.0" or "::" or "+")
        {
            var ip = LanAddress();
            if (ip is not null)
                StartupProgress.Line($"On other devices on your LAN, open http://{ip}:{settings.Port}/");
        }
    }

    static IPAddress BindAddress(string host) => host switch
    {
        "0.0.0.0" or "+" => IPAddress.Any,
        "::" => IPAddress.IPv6Any,
        _ => IPAddress.Parse(host),
    };

    static string SiteUrl(AppSettings settings, bool https)
    {
        var host = settings.Host is "0.0.0.0" or "::" or "+" or "127.0.0.1" or "::1"
            ? "localhost"
            : settings.Host;
        var scheme = https ? "https" : "http";
        var port = https ? settings.HttpsPort : settings.Port;
        return $"{scheme}://{host}:{port}/";
    }

    static string? LanAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
