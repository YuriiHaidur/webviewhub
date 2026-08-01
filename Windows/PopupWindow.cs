using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WebViewHub.Services;

namespace WebViewHub.Windows;

/// <summary>
/// Transient WebView2 popup spawned when a service page calls
/// <c>window.open()</c> — typically an OAuth flow (Sign in with
/// Google / Microsoft / GitHub / etc.). Shares the parent service's
/// WebView2 ProfileName so cookies and storage written here are
/// immediately visible to the parent — without this the auth callback
/// would land in a system browser and the parent page would stay
/// signed out.
///
/// Lifecycle: caller awaits <see cref="CoreReady"/>, then assigns
/// <c>e.NewWindow = popup.WebView.CoreWebView2</c> on the originating
/// <c>NewWindowRequested</c> args and completes the deferral. The popup
/// then navigates itself to <c>e.Uri</c> via the standard WebView2
/// opener-protocol; we never touch <see cref="WebView2.Source"/>
/// directly.
///
/// Closing: listens for <see cref="CoreWebView2.WindowCloseRequested"/>
/// so OAuth providers calling <c>window.close()</c> after a successful
/// flow dismiss the window automatically. Nested popups (popup-of-popup
/// — rare but exists) are chained recursively with the same profile.
/// </summary>
public class PopupWindow : FluentWindow
{
    private readonly string _profileName;
    private readonly string _parentServiceName;

    public WebView2 WebView { get; }

    /// <summary>Resolves once <see cref="WebView2.EnsureCoreWebView2Async"/>
    /// finishes. The caller awaits this before assigning
    /// <c>NewWindowRequestedEventArgs.NewWindow</c> — assigning a
    /// not-yet-initialized CoreWebView2 throws.</summary>
    public TaskCompletionSource<CoreWebView2> CoreReady { get; } = new();

    public PopupWindow(string profileName, string parentServiceName,
                       double? requestedWidth, double? requestedHeight,
                       double? requestedLeft, double? requestedTop)
    {
        _profileName = profileName;
        _parentServiceName = parentServiceName;

        // OAuth-popup-typical defaults when the page didn't set window
        // features. 500×650 fits Google/Microsoft/Apple sign-in forms
        // without internal scroll on standard DPI.
        Width = requestedWidth ?? 500;
        Height = requestedHeight ?? 650;
        MinWidth = 320;
        MinHeight = 400;

        if (requestedLeft.HasValue && requestedTop.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = requestedLeft.Value;
            Top = requestedTop.Value;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        Title = $"Sign in — {parentServiceName}";
        ShowInTaskbar = false;
        WindowBackdropType = WindowBackdropType.Mica;
        ExtendsContentIntoTitleBar = true;

        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);

        WebView = new WebView2();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar { Title = Title };
        Grid.SetRow(titleBar, 0);
        Grid.SetRow(WebView, 1);
        grid.Children.Add(titleBar);
        grid.Children.Add(WebView);
        Content = grid;

        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // SAME ProfileName as the parent ServiceWindow → cookies /
            // localStorage / IndexedDB shared. This is the entire reason
            // popups work for OAuth: the parent page reads its session
            // cookie set during the popup flow without any IPC.
            var options = App.WebViewEnvironment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _profileName;
            options.IsInPrivateModeEnabled = false;

            await WebView.EnsureCoreWebView2Async(App.WebViewEnvironment, options);

            // OAuth providers self-close via window.close() after success
            // — translate to closing our WPF window.
            WebView.CoreWebView2.WindowCloseRequested += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { Close(); }
                    catch (Exception ex) { Logger.Warn($"[Popup:{_parentServiceName}] Close on WindowCloseRequested failed: {ex.Message}"); }
                }));
            };

            // Nested popups (popup-of-popup) — chain into more PopupWindows
            // with the same profile so multi-step OAuth (e.g. Microsoft
            // tenant chooser → org login → MFA) keeps cookies coherent.
            WebView.CoreWebView2.NewWindowRequested += OnNestedNewWindowRequested;

            // Block in-popup external-window escapes via system browser
            // (current parent-window behavior) — keep the auth flow
            // entirely inside the WebView2 stack.

            Logger.Info($"[Popup:{_parentServiceName}] CoreWebView2 ready (profile={_profileName})");
            CoreReady.TrySetResult(WebView.CoreWebView2);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Popup:{_parentServiceName}] CoreWebView2 init failed", ex);
            CoreReady.TrySetException(ex);
            try { Close(); } catch { }
        }
    }

    private async void OnNestedNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var (w, h, l, t) = ReadFeatures(e.WindowFeatures);
            var nested = new PopupWindow(_profileName, _parentServiceName, w, h, l, t)
            {
                Owner = this
            };
            nested.Show();
            var core = await nested.CoreReady.Task;
            e.NewWindow = core;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Logger.Error($"[Popup:{_parentServiceName}] Nested popup spawn failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Reads optional size/position from a popup's <c>window.open()</c>
    /// features string. Returns nulls for any axis the page omitted so
    /// the constructor can fall back to OAuth-friendly defaults.
    /// </summary>
    public static (double? width, double? height, double? left, double? top) ReadFeatures(CoreWebView2WindowFeatures? features)
    {
        if (features == null) return (null, null, null, null);
        double? w = features.HasSize ? features.Width : null;
        double? h = features.HasSize ? features.Height : null;
        double? l = features.HasPosition ? features.Left : null;
        double? t = features.HasPosition ? features.Top : null;
        return (w, h, l, t);
    }
}
