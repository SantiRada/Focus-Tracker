using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using FocusTracker.Plugins;
using Microsoft.Win32;
using WpfApp        = System.Windows.Application;
using WpfMessageBox = System.Windows.MessageBox;
using WpfMsgButton  = System.Windows.MessageBoxButton;
using WpfMsgImage   = System.Windows.MessageBoxImage;
using WpfMsgResult  = System.Windows.MessageBoxResult;

namespace FocusTracker.Helpers;

/// <summary>
/// Manages the <c>focustracker://</c> custom URI scheme and named-pipe IPC
/// so that "Instalar en Focus Tracker" links in the web store work whether
/// the app is already running or not.
///
/// URI format:
///   focustracker://install?url=ENCODED_URL&amp;name=ENCODED_NAME&amp;id=PLUGIN_ID
///
/// Flow when user clicks "Instalar":
///   1. Browser opens focustracker://install?url=…
///   2. Windows launches FocusTracker.exe "focustracker://install?url=…"
///   3a. If app NOT running  → starts normally → OnStartup detects the URI arg
///       → calls HandleInstallAsync after the main window is ready.
///   3b. If app IS  running  → second instance sends the URI via named pipe
///       → pipe server (in running instance) calls HandleInstallAsync → second
///         instance exits immediately.
/// </summary>
public static class UriSchemeHandler
{
    // ── Constants ──────────────────────────────────────────────────────────
    public  const string SchemeName = "focustracker";
    private const string PipeName   = "FocusTracker_IPC_v1";

    // ── Registry: register focustracker:// ─────────────────────────────────

    /// <summary>
    /// Registers the <c>focustracker://</c> URI scheme in HKCU — no admin
    /// rights required. Idempotent: safe to call on every startup.
    /// </summary>
    public static void RegisterScheme()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule!.FileName;

            using var root = Registry.CurrentUser.CreateSubKey(
                $@"SOFTWARE\Classes\{SchemeName}");
            root.SetValue("",            $"URL:FocusTracker Protocol");
            root.SetValue("URL Protocol", "");

            using var iconKey = root.CreateSubKey("DefaultIcon");
            iconKey.SetValue("", $"\"{exe}\",0");

            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
        }
        catch
        {
            // Registry unavailable — URI install silently disabled.
        }
    }

    // ── Named-pipe server (primary / running instance) ─────────────────────

    private static CancellationTokenSource? _cts;

    /// <summary>
    /// Starts a background named-pipe listener.
    /// Whenever a secondary instance sends a URI string, <paramref name="onUri"/>
    /// is invoked on the WPF dispatcher thread.
    /// </summary>
    public static void StartPipeServer(Action<string> onUri)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Message,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(token);

                    using var sr = new StreamReader(pipe, Encoding.UTF8);
                    var uri = await sr.ReadToEndAsync(token);

                    if (!string.IsNullOrWhiteSpace(uri))
                        WpfApp.Current?.Dispatcher.Invoke(() => onUri(uri.Trim()));
                }
                catch (OperationCanceledException) { break; }
                catch { /* client disconnected or pipe error — restart loop */ }
            }
        }, token);
    }

    /// <summary>Stops the pipe server (call from App.OnExit).</summary>
    public static void StopPipeServer() => _cts?.Cancel();

    // ── Named-pipe client (secondary instance) ─────────────────────────────

    /// <summary>
    /// Sends <paramref name="uri"/> to the primary instance's pipe server.
    /// Returns <c>true</c> if the message was delivered within 2 seconds.
    /// </summary>
    public static bool TrySendUri(string uri)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);
            using var sw = new StreamWriter(pipe, Encoding.UTF8);
            sw.Write(uri);
            return true;
        }
        catch { return false; }
    }

    // ── URI parsing ────────────────────────────────────────────────────────

    /// <summary>Parsed parameters from a focustracker://install?… URI.</summary>
    public sealed record InstallArgs(string Url, string Name, string Id, string RawUri);

    /// <summary>
    /// Scans <paramref name="args"/> for a <c>focustracker://install?…</c>
    /// URI and returns its parsed parameters, or <c>null</c> if not found.
    /// </summary>
    public static InstallArgs? ParseInstallUri(string[] args)
    {
        var raw = args.FirstOrDefault(a =>
            a.StartsWith(SchemeName + "://", StringComparison.OrdinalIgnoreCase));
        if (raw == null) return null;

        try
        {
            // Strip custom scheme, treat as a synthetic https URL for Uri parsing
            var fake = "https://" + raw[(SchemeName.Length + 3)..];
            var uri  = new Uri(fake);

            if (!string.Equals(uri.Host, "install", StringComparison.OrdinalIgnoreCase))
                return null;

            // Parse query string without System.Web dependency
            var qs = uri.Query
                .TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2))
                .Where(p => p.Length == 2)
                .ToDictionary(
                    p => Uri.UnescapeDataString(p[0]),
                    p => Uri.UnescapeDataString(p[1]),
                    StringComparer.OrdinalIgnoreCase);

            if (!qs.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url))
                return null;

            qs.TryGetValue("name", out var name);
            qs.TryGetValue("id",   out var id);

            return new InstallArgs(url, name ?? "Plugin", id ?? "", raw);
        }
        catch { return null; }
    }

    // ── Download & install ─────────────────────────────────────────────────

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>
    /// Shows a confirmation dialog, downloads the .focusplugin file from
    /// <paramref name="args"/>.Url, and installs it via <see cref="PluginManager"/>.
    /// Must be called on the UI thread.
    /// </summary>
    public static async Task HandleInstallAsync(InstallArgs args)
    {
        var confirm = WpfMessageBox.Show(
            $"¿Instalar el plugin \"{args.Name}\"?\n\n" +
            $"Fuente: {args.Url}\n\n" +
            "Instalá solo plugins de fuentes en las que confíes.",
            "Instalar plugin — Focus Tracker",
            WpfMsgButton.YesNo,
            WpfMsgImage.Question,
            WpfMsgResult.No);

        if (confirm != WpfMsgResult.Yes) return;

        // Bring the app to front before showing progress
        App.ShowMainWindow();

        // Download to a temp file
        string tempPath;
        try
        {
            var fileName = Path.GetFileName(new Uri(args.Url).LocalPath);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !fileName.EndsWith(".focusplugin", StringComparison.OrdinalIgnoreCase))
                fileName = $"{SanitizeFileName(args.Id)}-install.focusplugin";

            tempPath = Path.Combine(Path.GetTempPath(), fileName);
            var bytes = await _http.GetByteArrayAsync(args.Url);
            await File.WriteAllBytesAsync(tempPath, bytes);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                $"No se pudo descargar el plugin:\n\n{ex.Message}",
                "Error de descarga",
                WpfMsgButton.OK,
                WpfMsgImage.Error);
            return;
        }

        // Install via PluginManager
        var error = PluginManager.Instance.Install(tempPath);
        try { File.Delete(tempPath); } catch { }

        if (error == null)
        {
            WpfMessageBox.Show(
                $"✓ \"{args.Name}\" instalado correctamente.\n\nEl plugin está activo.",
                "Plugin instalado",
                WpfMsgButton.OK,
                WpfMsgImage.Information);
        }
        else
        {
            WpfMessageBox.Show(
                $"Error al instalar \"{args.Name}\":\n\n{error}",
                "Error de instalación",
                WpfMsgButton.OK,
                WpfMsgImage.Error);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
