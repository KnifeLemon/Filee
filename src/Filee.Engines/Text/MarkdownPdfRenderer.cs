// Markdown PDF equations on Windows: Markdig -> offline MathJax SVG -> Chromium PDF.
// Chromium runs in a disposable profile; the user's browser session is never opened or changed.
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using Filee.Engines.Email;
using Filee.Engines.Infrastructure;
using Filee.Engines.Office;

namespace Filee.Engines.Text;

internal static class MarkdownPdfRenderer
{
    internal static string? LocateBrowser()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
        foreach (var relative in new[] { @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe" })
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static readonly Lazy<string> MathJax = new(() =>
    {
        using var resource = typeof(MarkdownPdfRenderer).Assembly.GetManifestResourceStream("Filee.Engines.MathJax.tex-svg-full.js")
            ?? throw new InvalidOperationException("The offline MathJax resource is missing.");
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    });

    internal static string PreparePage(string markdown, string input, string page)
    {
        var html = MarkdownConverter.ToHtml(markdown, Path.GetFullPath(input), page);
        // Reuse Filee's local-picture policy and its active HTML sanitizer before adding our own script.
        var bytes = Encoding.UTF8.GetBytes(html);
        html = Encoding.UTF8.GetString(LinkedContent.StripHtml(bytes, LinkedContent.PagePictures(page, input)) ?? bytes);
        var safe = EmlConverter.SafeHtml(html);
        html = MarkdownConverter.HtmlPage(Path.GetFileNameWithoutExtension(input), safe.Styles + safe.Body);
        var document = new HtmlParser().ParseDocument(html);
        foreach (var image in document.QuerySelectorAll("img").Where(i => string.IsNullOrEmpty(i.GetAttribute("src"))).ToArray())
            image.Parent?.RemoveChild(image);
        foreach (var math in document.QuerySelectorAll("div.math"))
        {
            var tex = math.TextContent.Trim();
            if (tex.StartsWith(@"\[") && tex.EndsWith(@"\]")) tex = tex[2..^2].Trim();
            // Typora accepts \\ between display lines without an explicit environment.
            if (tex.Contains(@"\\") && !tex.Contains(@"\begin{"))
                tex = @"\begin{gathered}" + tex + @"\end{gathered}";
            math.TextContent = @"\[" + tex + @"\]";
        }
        html = document.DocumentElement.OuterHtml;
        var scripts = "<style>" + PrintCss + "</style><script>" + Configuration + "</script><script>"
            + MathJax.Value.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase)
            + "</script><script>" + ReadyScript + "</script>";
        html = html.Replace("</body>", scripts + "</body>", StringComparison.OrdinalIgnoreCase);
        // All document resources are embedded. MathJax's full bundle has no font or extension downloads.
        return html.Replace("<head>", "<head><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; font-src data:; connect-src 'none'\">", StringComparison.OrdinalIgnoreCase);
    }

    private const string PrintCss = """
        @page{size:A4;margin:18mm}body{font-family:'Microsoft YaHei','Noto Sans CJK SC',sans-serif;font-size:10.5pt;max-width:none;margin:0;padding:0;line-height:1.65;color:#222}
        pre,code{font-family:Consolas,'Microsoft YaHei',monospace;font-size:9pt}pre{white-space:pre-wrap;overflow:visible;overflow-wrap:anywhere;word-break:break-word;line-height:1.5}
        h1,h2,h3,h4{break-after:avoid}tr,img,mjx-container{break-inside:avoid}thead{display:table-header-group}table{max-width:100%;font-size:inherit}mjx-container[display="true"]{overflow:visible!important}img{height:auto}
        mjx-container[jax="SVG"]{color:#333}
        /* MathJax adds a 3-unit glyph outline by default. Remove that outline, preserving mathematical rules and explicit bold glyphs. */
        mjx-container[jax="SVG"] path[data-c],mjx-container[jax="SVG"] use[data-c]{stroke-width:0!important}
        """;

    private const string Configuration = """
        window.fileeState={ready:false};window.fileeMathErrors=[];
        window.MathJax={loader:{load:[]},tex:{inlineMath:[['\\(','\\)']],displayMath:[['\\[','\\]']],packages:{'[-]':['autoload','require','noundefined','noerrors']},
          formatError:(jax,error)=>{window.fileeMathErrors.push(error.message);return jax.formatError(error)}},
          svg:{fontCache:'local'},options:{enableMenu:false,skipHtmlTags:['script','noscript','style','textarea','pre','code']},startup:{typeset:true}};
        """;

    private const string ReadyScript = """
        (async()=>{try{
          await MathJax.startup.promise;
          const errors=window.fileeMathErrors.concat(Array.from(document.querySelectorAll('[data-mjx-error],mjx-merror')).map(e=>e.getAttribute('data-mjx-error')||e.textContent));
          if(errors.length)throw new Error(errors.join('; '));
          await document.fonts.ready;
          await Promise.all(Array.from(document.images).map(i=>i.decode().catch(()=>i.remove())));
          window.fileeState={ready:true,formulas:document.querySelectorAll('mjx-container').length};
        }catch(e){window.fileeState={error:String(e)}}})();
        """;

    internal static async Task RenderAsync(string markdown, string input, string output, string work, CancellationToken cancellationToken)
    {
        var browser = LocateBrowser() ?? throw new InvalidOperationException("Markdown PDF needs Microsoft Edge or Google Chrome.");
        var folder = Directory.CreateDirectory(Path.Combine(work, "markdown-pdf-" + Guid.NewGuid().ToString("N"))).FullName;
        var page = Path.Combine(folder, "page.html");
        await File.WriteAllTextAsync(page, PreparePage(markdown, input, page), new UTF8Encoding(false), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        var endpoint = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string[] arguments = ["--headless=new", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
                     "--user-data-dir=" + Path.Combine(folder, "profile"), "--no-first-run", "--no-default-browser-check",
                     "--disable-extensions", "--disable-background-networking", "--disable-component-update", "about:blank"];
        var process = ProcessRunner.RunAsync(browser, arguments, TimeSpan.FromMinutes(3), token, onOutput: line =>
        {
            const string prefix = "DevTools listening on ";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                endpoint.TrySetResult(line[prefix.Length..].Trim());
        });
        try
        {
            if (await Task.WhenAny(endpoint.Task, process) == process)
            {
                var result = await process;
                throw new InvalidOperationException($"The PDF browser exited before it was ready (exit {result.ExitCode}).");
            }
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(await endpoint.Task.WaitAsync(token)), token);
            var protocol = new Protocol(socket, token);
            var target = await protocol.Call("Target.createTarget", new { url = "about:blank" });
            var attached = await protocol.Call("Target.attachToTarget", new { targetId = target["targetId"]!.GetValue<string>(), flatten = true });
            protocol.Session = attached["sessionId"]!.GetValue<string>();
            await protocol.Call("Page.enable", new { });
            await protocol.Call("Page.navigate", new { url = new Uri(page).AbsoluteUri });
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = await protocol.Call("Runtime.evaluate", new { expression = "window.fileeState || null", returnByValue = true });
                var value = state["result"]?["value"];
                if (value?["error"] is { } error) throw new InvalidOperationException("MathJax: " + error.GetValue<string>());
                if (value?["ready"]?.GetValue<bool>() == true) break;
                await Task.Delay(100, token);
            }
            await protocol.Call("Emulation.setEmulatedMedia", new { media = "print" });
            var pdf = await protocol.Call("Page.printToPDF", new { printBackground = true, preferCSSPageSize = true,
                displayHeaderFooter = false, transferMode = "ReturnAsBase64" });
            await File.WriteAllBytesAsync(output, Convert.FromBase64String(pdf["data"]!.GetValue<string>()), token);
        }
        finally
        {
            // ProcessRunner owns timeout, cancellation and process-tree cleanup, including when printing fails.
            await timeout.CancelAsync();
            try { await process; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
    }

    private sealed class Protocol(ClientWebSocket socket, CancellationToken token)
    {
        private int _id;
        public string? Session { get; set; }
        public async Task<JsonNode> Call(string method, object parameters)
        {
            var id = ++_id;
            var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(parameters) };
            if (Session is not null) request["sessionId"] = Session;
            var payload = Encoding.UTF8.GetBytes(request.ToJsonString());
            await socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, token);
            var buffer = new byte[65536];
            while (true)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), token);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException("The PDF browser closed its connection.");
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                var response = JsonNode.Parse(message.ToArray())!;
                if (response["id"]?.GetValue<int>() != id) continue;
                if (response["error"] is { } error) throw new InvalidOperationException("PDF browser: " + error.ToJsonString());
                return response["result"] ?? new JsonObject();
            }
        }
    }
}
