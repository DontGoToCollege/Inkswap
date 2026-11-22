using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Inkswap;

public partial class MainWindow : Window
{
    static readonly string Origin = $"https://{AppInfo.Host}/";

    readonly bool _smoke;
    readonly string? _smokeScript;
    readonly WindowSettings _settings = WindowSettings.Load();
    readonly List<string> _blocked = new();
    CoreWebView2Environment? _env;
    bool _dirty;   // the page says it has unsaved work (see OnWebMessage)

    public MainWindow(bool smoke, string? smokeScript = null)
    {
        _smoke = smoke;
        _smokeScript = smokeScript;
        InitializeComponent();
        Title = AppInfo.Name;
        Background = Brush(AppInfo.Background);
        Status.Foreground = Brush(AppInfo.Text);
        ApplyPlacement();
        if (_smoke)
        {
            // self-checks run behind the owner's work: off-screen and never activated
            ShowActivated = false;
            WindowState = WindowState.Normal;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = SystemParameters.VirtualScreenLeft - Width - 200;
            Top = 0;
        }
        SourceInitialized += (_, _) => SetTitleBarColors();
        Loaded += async (_, _) => await InitWebViewAsync();
        Closing += (_, e) =>
        {
            if (_dirty && !_smoke && MessageBox.Show(this, "You have unsaved changes. Close anyway?", AppInfo.Name,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            SavePlacement();
        };
        Closed += (_, _) => Engine.Stop();
    }

    async Task InitWebViewAsync()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Status.Text = "Microsoft Edge WebView2 Runtime is missing.\nIt ships with Windows 11; on older Windows install the Evergreen runtime once.";
            PortablePaths.Log("WebView2 runtime not found");
            return;
        }

        try
        {
            var options = new CoreWebView2EnvironmentOptions(
                "--disable-background-networking --disable-component-update --disable-domain-reliability " +
                "--no-pings --disable-features=msSmartScreenProtection,AutofillServerCommunication,CalculateNativeWinOcclusion");
            _env = await CoreWebView2Environment.CreateAsync(null, PortablePaths.WebViewDir, options);
            var bg = ((SolidColorBrush)Background).Color;
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, bg.R, bg.G, bg.B);
            await Web.EnsureCoreWebView2Async(_env);
        }
        catch (Exception ex)
        {
            Status.Text = "Could not start the web view:\n" + ex.Message;
            PortablePaths.Log("webview init failed: " + ex);
            return;
        }

        var core = Web.CoreWebView2;
        var devtools = Environment.GetCommandLineArgs().Contains("--devtools", StringComparer.OrdinalIgnoreCase);
        var s = core.Settings;
        s.AreDevToolsEnabled = devtools;
        s.AreDefaultContextMenusEnabled = devtools;
        s.AreBrowserAcceleratorKeysEnabled = false;   // F5/ctrl+R/ctrl+F etc. go to the app instead
        s.IsStatusBarEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsReputationCheckingRequired = false;

        // every request goes through here: embedded files are served, anything else is refused
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.NavigationStarting += (_, e) =>
        {
            if (!IsOurs(e.Uri))
            {
                e.Cancel = true;
                PortablePaths.Log("navigation blocked: " + e.Uri);
            }
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.DownloadStarting += OnDownloadStarting;
        // The page is our own embedded app: let it write to files the user picked, and save a second
        // file in one session, without a browser prompt.
        core.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind is CoreWebView2PermissionKind.FileReadWrite or CoreWebView2PermissionKind.MultipleAutomaticDownloads
                && IsOurs(e.Uri))
                e.State = CoreWebView2PermissionState.Allow;
        };
        core.NavigationCompleted += async (_, e) =>
        {
            Status.Visibility = Visibility.Collapsed;
            if (!e.IsSuccess) PortablePaths.Log("navigation failed: " + e.WebErrorStatus);
            if (_smoke) await RunSmokeAsync(e.IsSuccess);
        };
        core.Navigate(Origin + "index.html");
    }

    /// <summary>Our own pages, and the blob: files they make for saving and previewing.</summary>
    static bool IsOurs(string uri) =>
        uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) ||
        uri.StartsWith("blob:" + Origin, StringComparison.OrdinalIgnoreCase);

    void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var uri = new Uri(e.Request.Uri);
        if (uri.Scheme is "blob" or "data") return;   // made by the page itself, never from the network
        if (uri.Scheme == "https" && uri.Host.Equals(AppInfo.Host, StringComparison.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                _ = ServeApiAsync(e, uri.AbsolutePath);
                return;
            }
            var stream = WebAssets.Open(uri.AbsolutePath, out var contentType);
            e.Response = stream is null
                ? _env!.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain")
                : _env!.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {contentType}\r\nCache-Control: no-cache");
            return;
        }
        lock (_blocked) _blocked.Add(e.Request.Uri);
        PortablePaths.Log("request blocked (offline app): " + e.Request.Uri);
        e.Response = _env!.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
    }

    /// <summary>The page's link to the local AI. Answers JSON; never reaches anything but 127.0.0.1.</summary>
    async Task ServeApiAsync(CoreWebView2WebResourceRequestedEventArgs e, string path)
    {
        using var deferral = e.GetDeferral();
        var code = 200;
        string json;
        try
        {
            var body = "";
            if (e.Request.Content is { } content)
                using (var reader = new StreamReader(content)) body = await reader.ReadToEndAsync();
            switch (path)
            {
                case "/api/status":
                    json = JsonSerializer.Serialize(Engine.Status());
                    break;
                case "/api/fonts":
                    json = JsonSerializer.Serialize(Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().Order());
                    break;
                case "/api/start":
                    Engine.Start(JsonDocument.Parse(body).RootElement.GetProperty("model").GetString() ?? "");
                    json = JsonSerializer.Serialize(Engine.Status());
                    break;
                case "/api/chat":
                    (code, json) = await Engine.Chat(body);
                    break;
                default:
                    (code, json) = (404, "{\"error\":\"no such endpoint\"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            PortablePaths.Log($"api {path}: {ex}");
            (code, json) = (500, JsonSerializer.Serialize(new { error = ex.Message }));
        }
        e.Response = _env!.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(json)), code,
            code == 200 ? "OK" : "Error", "Content-Type: application/json; charset=utf-8\r\nCache-Control: no-store");
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "dirty") _dirty = root.GetProperty("value").GetBoolean();
        }
        catch (Exception ex)
        {
            PortablePaths.Log("bad web message: " + ex.Message);
        }
    }

    void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // files the app saves get a normal Save dialog instead of the browser download bubble
        e.Handled = true;
        var suggested = Path.GetFileName(e.ResultFilePath);
        if (_smoke)
        {
            e.ResultFilePath = Path.Combine(PortablePaths.DataDir, suggested);
            return;
        }
        var deferral = e.GetDeferral();
        Dispatcher.BeginInvoke(() =>
        {
            using (deferral)
            {
                var ext = Path.GetExtension(suggested).TrimStart('.').ToLowerInvariant();
                var dlg = new SaveFileDialog
                {
                    FileName = suggested,
                    Filter = (ext.Length > 0 ? $"{ext.ToUpperInvariant()} file (*.{ext})|*.{ext}|" : "") + "All files (*.*)|*.*",
                };
                if (dlg.ShowDialog(this) == true) e.ResultFilePath = dlg.FileName;
                else e.Cancel = true;
            }
        });
    }

    /// <summary>
    /// Self-check behind --smoke: runs smoke.js against the loaded app, saves a file the way the app does,
    /// writes PASS or FAIL with the details to smoke.txt in the data folder, and exits.
    /// </summary>
    async Task RunSmokeAsync(bool navOk)
    {
        var core = Web.CoreWebView2;
        var download = Path.Combine(PortablePaths.DataDir, "smoke-download.txt");
        File.Delete(download);
        string probe;
        var probeOk = false;
        try
        {
            using var js = _smokeScript is null
                ? new StreamReader(typeof(MainWindow).Assembly.GetManifestResourceStream("smoke.js")!)
                : new StreamReader(_smokeScript);
            // userGesture: the page treats the script's clicks as the user's, as sound and saving require
            var args = JsonSerializer.Serialize(new { expression = js.ReadToEnd(), awaitPromise = true, returnByValue = true, userGesture = true });
            using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args));
            probeOk = !result.RootElement.TryGetProperty("exceptionDetails", out var error);
            probe = probeOk ? result.RootElement.GetProperty("result").GetProperty("value").ToString() : error.ToString();

            await core.ExecuteScriptAsync(
                "(()=>{const a=document.createElement('a');a.href=URL.createObjectURL(new Blob(['smoke']));" +
                "a.download='smoke-download.txt';document.body.appendChild(a);a.click();a.remove();})()");
            for (var i = 0; i < 50 && !(File.Exists(download) && new FileInfo(download).Length > 0); i++) await Task.Delay(100);

            // what the page looked like at the end, captured from the web view itself (no screen grab)
            using var shot = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}"));
            File.WriteAllBytes(Path.Combine(PortablePaths.DataDir, "smoke.png"), Convert.FromBase64String(shot.RootElement.GetProperty("data").GetString()!));
        }
        catch (Exception ex)
        {
            probeOk = false;
            probe = "smoke failed: " + ex.Message;
        }
        var saved = File.Exists(download) && File.ReadAllText(download) == "smoke";
        string[] blocked;
        lock (_blocked) blocked = _blocked.ToArray();
        var pass = navOk && probeOk && saved && blocked.Length == 0;
        File.WriteAllText(Path.Combine(PortablePaths.DataDir, "smoke.txt"),
            $"{(pass ? "PASS" : "FAIL")}\nnavigation ok: {navOk}\nembedded assets: {WebAssets.Count}\ndata dir: {PortablePaths.DataDir}\n" +
            $"probe ok: {probeOk}\nprobe: {probe}\nfile save ok: {saved}\nblocked requests: {blocked.Length}\n" + string.Join("\n", blocked));
        Close();
    }

    /* ---------------- window ---------------- */

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    /// <summary>Title bar in the app's own colors (Windows 11; older Windows keeps its default bar).</summary>
    void SetTitleBarColors()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var bg = ((SolidColorBrush)Background).Color;
        var text = ((SolidColorBrush)Status.Foreground).Color;
        int dark = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) < 128 ? 1 : 0;
        int caption = bg.R | (bg.G << 8) | (bg.B << 16);
        int captionText = text.R | (text.G << 8) | (text.B << 16);
        DwmSetWindowAttribute(hwnd, 20 /* dark mode */, ref dark, sizeof(int));
        DwmSetWindowAttribute(hwnd, 35 /* caption color */, ref caption, sizeof(int));
        DwmSetWindowAttribute(hwnd, 36 /* caption text */, ref captionText, sizeof(int));
    }

    void ApplyPlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
        {
            // only restore the position if it is still on a visible screen
            var vl = SystemParameters.VirtualScreenLeft;
            var vt = SystemParameters.VirtualScreenTop;
            var vr = vl + SystemParameters.VirtualScreenWidth;
            var vb = vt + SystemParameters.VirtualScreenHeight;
            if (_settings.Left > vl - 50 && _settings.Left < vr - 100 && _settings.Top > vt - 10 && _settings.Top < vb - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.Left;
                Top = _settings.Top;
            }
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    void SavePlacement()
    {
        if (_smoke) return;
        var r = WindowState != WindowState.Normal ? RestoreBounds : new Rect(Left, Top, Width, Height);
        if (!r.IsEmpty)
        {
            _settings.Left = r.Left;
            _settings.Top = r.Top;
            _settings.Width = r.Width;
            _settings.Height = r.Height;
        }
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }
}
