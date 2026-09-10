using System;
using System.IO;
using System.Reflection;
using System.Text;
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
}
