# WebViewHub

Each web app runs in its own Windows window, with its own tray icon, hotkey and
login session. Uses the WebView2 runtime that ships with Windows instead of
bundling a browser, so the install stays small and memory scales with the pages
you actually open.

![The hub, listing configured services](assets/hub.png)

Personal project, published as is. There is no installer, no release pipeline
and no support.

Written mostly by prompting Claude. Design decisions, testing and review were
done by hand, but the code shows its origin in places, mainly in comment
density.

## Features

- One window per service, each with its own taskbar entry, tray icon and saved
  position
- Peek mode: the window hides when it loses focus, so a hotkey turns a service
  into an overlay
- Multiple accounts per service, each in a separate WebView2 profile, switched
  from the title bar menu
- A global hotkey per service. Double Ctrl+C opens a translator with the copied
  text
- Unread counts parsed from the page title with a regex, drawn on the tray icon
  and as a taskbar overlay
- Custom CSS per service, optionally only in dark mode. Handles UserCSS syntax
  (`@-moz-document`, `@var`), so most styles from userstyles.world work as-is
- Icons pulled from the site, WebCatalog or macOSicons, then cropped, masked and
  written as a multi-frame `.ico` so Windows picks a DPI-matched frame
- Portable: config, logs, icons and browser profiles live in `Data/` next to the
  executable. Nothing else is written outside it except the autostart entry and
  protocol registrations, if you enable them

## Requirements

- Windows 10 1809 or newer
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0),
  unless you use the self-contained build from
  [Releases](https://github.com/YuriiHaidur/webviewhub/releases)
- WebView2 Runtime, preinstalled on Windows 11 and current Windows 10, otherwise
  from [Microsoft](https://developer.microsoft.com/microsoft-edge/webview2/)

## Build

```powershell
git clone https://github.com/YuriiHaidur/webviewhub.git
cd webviewhub
dotnet build WebViewHub.csproj -c Release
```

The executable lands in `bin/Release/net8.0-windows/`. Run it from there; it
creates `Data/` alongside itself on first launch.

```powershell
dotnet test WebViewHub.Tests/WebViewHub.Tests.csproj
```

## Layout

| Area | Files |
|---|---|
| Startup, single instance, WebView2 environment | `App.xaml.cs` |
| Window lifecycle, hub, profile switching | `Services/WindowManager.cs` |
| Service window, WebView2 host, tray icon | `Windows/ServiceWindow.xaml.cs` |
| Config model and per-service settings | `Models/Models.cs` |
| Icon fetch, normalisation, `.ico` generation | `Helpers/IconHelper.cs`, `Services/FaviconService.cs` |
| Global hotkeys, low-level keyboard hook | `Services/HotkeyManager.cs`, `Services/LowLevelKeyboardHook.cs` |

Services are isolated by WebView2 profile: one shared `CoreWebView2Environment`
with a per-profile `ProfileName`. Cookies and storage never cross between
services, or between accounts of the same service.

## Implementation notes

Four things in the code look wrong until you know the constraint behind them.

`ProfileName` is fixed when the WebView2 controller is created and cannot be
changed afterwards. Switching accounts therefore rebuilds the window instead of
swapping the session in place.

WebView2 is an `HwndHost`, and WPF cannot draw above one. A `ContentDialog`
centred over the page renders behind it while still capturing WPF input, which
looks like a freeze: the page keeps working and the title bar goes dead. Prompts
are separate windows for that reason.

The low-level keyboard hook runs on its own thread and the logger never blocks.
A blocking write inside the hook callback makes Windows drop keystrokes
system-wide.

`SetForegroundWindow` is refused when the process lacks foreground privilege,
which is the normal case when a hook rather than a registered hotkey triggered
the call. Activation goes through `AttachThreadInput`.

## Stack

| | |
|---|---|
| Language, runtime | C# 12, .NET 8 (`net8.0-windows`), nullable enabled |
| UI | WPF with [WPF-UI](https://github.com/lepoco/wpfui) 4.3.0 for Fluent controls, Mica backdrop and runtime theme switching |
| Browser | [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) 1.0.2792.45 |
| Tray | [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) 2.0.124 |
| Tests | xUnit 2.9.2 |
| Interop | Win32 P/Invoke: `RegisterHotKey`, `WH_KEYBOARD_LL`, `SHGetPropertyStoreForWindow`, `WM_SETICON`, DWM cloaking |
| Storage | JSON files via `System.Text.Json`. No database, no network calls except icon fetching |

Build output is a WPF executable plus the three NuGet packages above. No
Electron, no Node toolchain.

## License

[MIT](LICENSE)
