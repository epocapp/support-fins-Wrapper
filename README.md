# PrintFins

**Version 1.0**

Windows host for [Support Fins](https://github.com/gittrahan/support-fins): a C# executable that keeps a local copy of that repository up to date, serves its `web/` folder, and opens the site in a browser or an in-app WebView.

There is no Python. The original project's `dev-server.py` is replaced by this app.

## What happens with the original repo

On every start, PrintFins talks to the Git remote in `repositoryUrl` (by default `https://github.com/gittrahan/support-fins.git`):

1. If `cloneDirectory` does not exist, it clones the repository (`git clone --depth 1`).
2. If it already exists, it compares the local `HEAD` commit with the remote `HEAD`.
   - Same commit: nothing is downloaded.
   - Different commit: it fetches the update, resets the local clone to that commit, and shows a window listing the new commit titles (one per line).
3. After a new clone or update, it can run an antivirus scan on the downloaded folder (`scanDownloads`).
4. It serves the folder given by `webRoot` (default `web`) over HTTP and HTTPS.
5. It opens the site according to `display`.

The cloned repository is **not** packed into the executable. It is downloaded next to the exe the first time you run it (or whenever it is missing), then reused when it is already current.

In **webview** mode, the in-app window may only reach loopback (`localhost` / `127.0.0.1` / `::1`). Requests to the internet or the local network are blocked.

## Requirements

- Windows
- [Git](https://git-scm.com/) on `PATH`
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build
- Microsoft Edge WebView2 Runtime for `display: "webview"` (included on current Windows 10/11)
- For HTTPS: ASP.NET Core development certificate trusted once with `dotnet dev-certs https --trust`

## Build and publish

```bash
dotnet publish PrintFins.sln -c Release
```

Output:

`bin\Release\net8.0-windows\win-x64\publish\PrintFins.exe`

That file is self-contained. Default settings are embedded inside it. The Support Fins repo is still downloaded at runtime.

## Override settings

Defaults live in the project's [`settings.json`](settings.json) and are compiled into the exe.

To override them, place a `settings.json` **next to** `PrintFins.exe`. Embedded defaults are loaded first; only properties present in the external file override those defaults. Everything else keeps the built-in value. Relative paths are resolved from the executable's directory.

Comments (`//`) and trailing commas are allowed.

### Settings reference

| Property | Meaning |
| --- | --- |
| `repositoryUrl` | Git URL of Support Fins (or another compatible fork). |
| `cloneDirectory` | Folder that receives the clone. Relative paths are next to the exe. Local commit = that folder's Git `HEAD`. |
| `webRoot` | Folder inside the clone to serve (upstream uses `web`). Absolute paths are allowed. |
| `host` | Bind address. `127.0.0.1` = this computer only. `0.0.0.0` = reachable on the LAN. |
| `port` | HTTP listen port. System browser mode opens this URL. |
| `httpsPort` | HTTPS listen port. WebView mode opens this URL. Must differ from `port`. |
| `display` | `browser` opens the default browser (HTTP). `webview` opens a maximized in-app window (HTTPS, network-locked). |
| `scanDownloads` | When `true`, scan a folder that was just downloaded before serving it. Up-to-date clones are not scanned again. |
| `antivirusCommand` | Scanner command. `{path}` is replaced with the clone folder. Exit code `0` = clean; any other code deletes the download and stops. Default is empty = Windows Defender (`MpCmdRun`). |

Example override next to the exe (only change display; everything else stays at the embedded defaults):

```json
{
  "display": "browser"
}
```

ESET example for `antivirusCommand` (adjust the install path if needed):

```text
"C:\Program Files\ESET\ESET Security\ecls.exe" --action=none "{path}"
```

## Run

```bash
PrintFins.exe
```

Or from the project after a Debug build:

```bash
dotnet run --project PrintFins.csproj
```

With `display: "webview"`, only the app window is shown (no console). Close that window to stop.

With `display: "browser"`, a console stays open for logs. Stop with Ctrl+C.
