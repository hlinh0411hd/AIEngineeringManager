using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Employee.Infrastructure;

// Screenshots of a web project as a real browser renders it, taken by the manager itself (outside the
// AI workers' sandbox, which cannot start a browser). The project is served read-only over a
// loopback HTTP server only for the time of the shot, then opened in headless Edge/Chrome. Testers
// and fix steps look at the images, so a layout that only breaks in a browser is seen.
public static class PageSnapshot
{
    public sealed record Shot(string Name, string Path);
    // Headless browsers keep a minimum window width (about 500 CSS px for Edge), so a 390-wide window
    // still lays the page out wider and the screenshot crops it. The phone view therefore loads the
    // page in a 390x844 frame inside a wider window: media queries and sizes inside see a real phone width.
    private static readonly (string Name, int Width, int Height, bool Framed)[] Viewports = [("desktop", 1280, 800, false), ("phone", 390, 844, true)];
    public const string PhoneFrame = "__aiemployee_phone.html";

    // The page to open: index.html at the root, or in a usual build/public folder.
    public static string? EntryPage(string root)
    {
        foreach (var relative in new[] { "index.html", "public/index.html", "dist/index.html", "www/index.html", "web/index.html" })
            if (File.Exists(Path.Combine(root, relative)))
                return relative;
        return null;
    }

    public static string? Browser()
    {
        var folders = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) };
        foreach (var folder in folders.Where(f => f.Length > 0))
            foreach (var relative in new[] { @"Microsoft\Edge\Application\msedge.exe", @"Google\Chrome\Application\chrome.exe" })
                if (File.Exists(Path.Combine(folder, relative)))
                    return Path.Combine(folder, relative);
        return null;
    }

    // Empty when the project has no web page or no browser is installed; never throws for those.
    public static async Task<IReadOnlyList<Shot>> TakeAsync(string root, string name, string scratch, CancellationToken token)
    {
        var entry = EntryPage(root);
        var browser = Browser();
        if (entry == null || browser == null)
            return [];
        var folder = Path.Combine(root, ".aiemployee", "screenshots");
        Directory.CreateDirectory(folder);
        using var server = new StaticServer(root);
        var shots = new List<Shot>();
        foreach (var (viewport, width, height, framed) in Viewports)
        {
            var file = Path.Combine(folder, $"{name}-{viewport}.png");
            var page = entry.Replace('\\', '/');
            var url = server.Url + (framed ? PhoneFrame + "?page=" + Uri.EscapeDataString(page) : page);
            if (await ShootAsync(browser, url, file, framed ? width + 160 : width, height, scratch, token))
                shots.Add(new(framed ? $"{viewport} {width}x{height} (the phone screen is the left {width}px; the magenta strip on the right is outside it)" : $"{viewport} {width}x{height}", file));
        }
        return shots;
    }

    // One headless screenshot of a URL into file; false when the browser made none.
    private static async Task<bool> ShootAsync(string browser, string url, string file, int width, int height, string scratch, CancellationToken token)
    {
        File.Delete(file);
        // A fresh profile per shot: a browser still closing on a shared profile takes over the next launch.
        var profile = Path.Combine(scratch, "browser-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        var info = new ProcessStartInfo(browser) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--mute-audio", "--user-data-dir=" + profile, $"--window-size={width},{height}", "--virtual-time-budget=4000", "--screenshot=" + file, url })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        _ = process.StandardOutput.ReadToEndAsync(token);
        _ = process.StandardError.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        try { Directory.Delete(profile, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return File.Exists(file) && new FileInfo(file).Length > 0;
    }

    public sealed record ArtImage(string Label, string Path);

    // One picture of the project's existing art (top) and the new art (below), every image scaled
    // by whole steps with hard pixels, so a reviewer sees scale, style and frame-to-frame changes side
    // by side. Seen live: an animation made from a poor earlier output, with frames of different sizes,
    // passed a tester who only checked sizes and wiring. Null when there is nothing to show or no browser.
    public static async Task<string?> ArtSheetAsync(IReadOnlyList<ArtImage> reference, IReadOnlyList<ArtImage> made, string file, string scratch, CancellationToken token)
    {
        var browser = Browser();
        if (browser == null || made.Count == 0)
            return null;
        var folder = Path.Combine(scratch, "art-sheet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var html = new StringBuilder("<!doctype html><html><head><meta charset='utf-8'><style>body{margin:0;padding:12px;background:#3c4650;color:#fff;font:14px sans-serif}h2{margin:8px 0}figure{display:inline-block;margin:0 16px 16px 0;vertical-align:top}img{image-rendering:pixelated;display:block;background:repeating-conic-gradient(#556 0 25%,#445 0 50%) 0 0/16px 16px}figcaption{max-width:520px}</style></head><body>");
            var height = 24;
            var index = 0;
            void Section(string title, IReadOnlyList<ArtImage> images)
            {
                var shown = images.Where(image => PngSize(image.Path) != null).ToArray();
                if (shown.Length == 0)
                    return;
                html.Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2>");
                var x = 0;
                var rowHeight = 0;
                foreach (var image in shown)
                {
                    var (w, h) = PngSize(image.Path)!.Value;
                    // Small art up by whole steps to 128-256 px tall; large art down to 256 px tall.
                    var scale = h >= 256 ? 256.0 / h : Math.Max(1, Math.Min(8, 256 / h));
                    if (w * scale > 1300)
                        scale = 1300.0 / w;
                    int shownWidth = (int)(w * scale), shownHeight = (int)(h * scale);
                    var name = "img" + index++ + ".png";
                    File.Copy(image.Path, Path.Combine(folder, name));
                    html.Append($"<figure><img src='{name}' width='{shownWidth}' height='{shownHeight}'><figcaption>{WebUtility.HtmlEncode(image.Label)} ({w}x{h})</figcaption></figure>");
                    // Rough layout height: figures wrap at the 1400 px window width.
                    if (x + shownWidth > 1370)
                    {
                        height += rowHeight;
                        x = 0;
                        rowHeight = 0;
                    }
                    x += shownWidth + 16;
                    rowHeight = Math.Max(rowHeight, shownHeight + 70);
                }
                height += rowHeight + 40;
            }
            Section("Existing art of the project (the style and scale to match)", reference);
            Section("New art made in this task", made);
            html.Append("</body></html>");
            File.WriteAllText(Path.Combine(folder, "index.html"), html.ToString());
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var server = new StaticServer(folder);
            return await ShootAsync(browser, server.Url + "index.html", file, 1400, Math.Clamp(height, 400, 4000), scratch, token) ? file : null;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Width and height from a PNG header, or null when the file is not a readable PNG.
    public static (int Width, int Height)? PngSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[24];
            if (stream.Read(header, 0, 24) < 24 || header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71)
                return null;
            int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20));
            return width > 0 && height > 0 && width < 20000 && height < 20000 ? (width, height) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // GET-only static files from one folder, on a free loopback port, for the life of the object.
    private sealed class StaticServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly string root;
        public string Url { get; }

        public StaticServer(string folder)
        {
            root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            _ = Task.Run(Accept);
        }

        private async Task Accept()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); } catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
                    var line = await reader.ReadLineAsync();
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    var parts = line?.Split(' ') ?? [];
                    var target = parts.Length > 1 ? parts[1] : "/";
                    var path = Uri.UnescapeDataString(target.Split('?', '#')[0]).TrimStart('/');
                    if (path == PhoneFrame)
                    {
                        var page = Uri.UnescapeDataString(System.Text.RegularExpressions.Regex.Match(target, "[?&]page=([^&#]*)").Groups[1].Value);
                        var html = "<!doctype html><html><body style='margin:0;background:#ff00ff'><iframe src='/" + WebUtility.HtmlEncode(page) + "' style='display:block;border:0;width:390px;height:844px;background:#fff'></iframe></body></html>";
                        var frame = Encoding.UTF8.GetBytes(html);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + frame.Length + "\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
                        await stream.WriteAsync(frame);
                        return;
                    }
                    var full = Path.GetFullPath(Path.Combine(root, path.Length == 0 ? "index.html" : path));
                    if (Directory.Exists(full))
                        full = Path.Combine(full, "index.html");
                    var ok = parts.Length > 0 && parts[0] == "GET" && full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
                    var body = ok ? await File.ReadAllBytesAsync(full) : Encoding.UTF8.GetBytes("Not found");
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(ok ? Type(full) : "text/plain")}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head);
                    await stream.WriteAsync(body);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string Type(string file) => Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" or ".map" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".ogg" => "audio/ogg",
            ".wasm" => "application/wasm",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
