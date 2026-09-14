using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace StreamReactive;

internal sealed class StreamEventBehavior : WebSocketBehavior
{
    protected override void OnMessage(MessageEventArgs e)
    {
        if (e.Data == null)
            return;

        // WebSocketSharp raises this callback on a thread-pool thread. Every
        // effect it drives (StartCoroutine, GameObject caches, note prefab
        // lookups, config mutations) requires the Unity main thread, so hop
        // over before touching anything.
        var data = e.Data;
        RuntimeHooks.RunOnMainThread(() =>
        {
            try
            {
                EventDispatcher.ProcessRaw(data);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warn($"Failed to process WebSocket message: {ex.Message}");
            }
        });
    }
}

internal sealed class WebSocketServer : IDisposable
{
    private HttpServer? _server;

    public int Port { get; }
    public bool IsRunning => _server != null;

    public WebSocketServer(int port)
    {
        Port = port;
    }

    public void Start()
    {
        if (_server != null) return;

        _server = new HttpServer(Port);

        _server.AddWebSocketService<StreamEventBehavior>("/stream");

        _server.OnGet += (sender, e) =>
        {
            var path = e.Request.Url!.AbsolutePath;
            string html;
            switch (path)
            {
                case "/":
                    html = GetDashboardHtml(Port);
                    break;
                case "/generator":
                    html = GetResourceHtml("StreamReactive.Resources.generator.html", Port);
                    break;
                case "/debug":
                    html = GetResourceHtml("StreamReactive.Resources.debug.html", Port);
                    break;
                default:
                    e.Response.StatusCode = 404;
                    e.Response.WriteContent(Encoding.UTF8.GetBytes("Not Found"));
                    return;
            }

            e.Response.ContentType = "text/html; charset=utf-8";
            e.Response.WriteContent(Encoding.UTF8.GetBytes(html));
        };

        _server.Start();

        ExtractGeneratorFile();

        Plugin.Log.Info($"WebSocket server started on ws://localhost:{Port}/stream");
        Plugin.Log.Info($"Test dashboard available at http://localhost:{Port}/");
        Plugin.Log.Info("Configure Streamer.bot to connect to this WebSocket endpoint and send JSON events.");
    }

    public void Stop()
    {
        if (_server == null) return;

        try
        {
            _server.Stop();
        }
        catch { }

        _server = null;
        Plugin.Log.Info("WebSocket server stopped.");
    }

    public void Dispose()
    {
        Stop();
    }

    private static string GetDashboardHtml(int port)
    {
        return GetResourceHtml("StreamReactive.Resources.test-dashboard.html", port);
    }

    private static string GetResourceHtml(string resourceName, int port)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            Plugin.Log.Error($"Embedded HTML resource not found: {resourceName}");
            return "<html><body><h1>Resource not found</h1></body></html>";
        }

        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        return html.Replace("PORT_PLACEHOLDER", port.ToString());
    }

    // Writes a standalone copy of the generator page to UserData/StreamReactive
    // so it can be opened in a browser while setting up Streamer.bot, even
    // without the game running. The tab bar is stripped out and the configured
    // WebSocket port is baked in, so the page always matches the mod's settings
    // at write time. Overwriting on every start keeps the on-disk copy in sync
    // with the mod's version.
    private void ExtractGeneratorFile()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream("StreamReactive.Resources.generator.html");
            if (stream == null)
            {
                Plugin.Log.Warn("Embedded resource not found: StreamReactive.Resources.generator.html");
                return;
            }

            using var reader = new StreamReader(stream);
            var html = reader.ReadToEnd();
            html = Regex.Replace(html, @"\s*<nav>.*?</nav>\s*", "", RegexOptions.Singleline);
            html = html.Replace("PORT_PLACEHOLDER", Port.ToString());

            var dir = Path.Combine(Environment.CurrentDirectory, "UserData", "StreamReactive");
            Directory.CreateDirectory(dir);
            var destPath = Path.Combine(dir, "generator.html");
            File.WriteAllText(destPath, html, new UTF8Encoding(false));
            Plugin.Log.Info($"Message generator written to {destPath}");
        }
        catch (Exception ex)
        {
            Plugin.Log.Warn($"Could not extract generator file: {ex.Message}");
        }
    }
}
