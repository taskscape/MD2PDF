using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Playwright;

internal static class Program
{
    private static readonly string[] EntryCandidates = ["default.md", "readme.md", "index.md"];
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"
    };
    private static readonly HashSet<string> MermaidExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mmd", ".mermaid" };
    private static readonly Regex MarkdownLinkRegex = new(
        @"(?<bang>!)?\[(?<text>[^\]\r\n]*)\]\((?<url><[^>]+>|[^\s\)]+)(?:\s+[""'][^)]*[""'])?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.Usage);
                return 0;
            }

            var root = Path.GetFullPath(options.InputDirectory);
            if (!Directory.Exists(root))
            {
                Console.Error.WriteLine($"Input directory does not exist: {root}");
                return 2;
            }

            var entry = options.EntryFile is not null
                ? ResolveRequiredFile(root, options.EntryFile)
                : FindEntryFile(root);

            if (entry is null)
            {
                Console.Error.WriteLine($"No entry file found in {root}. Expected one of: {string.Join(", ", EntryCandidates)}");
                return 2;
            }

            var output = Path.GetFullPath(options.OutputPdf ?? DefaultOutputPath(root));
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);

            var pipeline = new MarkdownPipelineBuilder()
                .UseAdvancedExtensions()
                .UseAutoIdentifiers()
                .Build();

            var warnings = new List<string>();
            var orderedDocuments = DocumentGraph.Build(root, entry, pipeline, warnings);
            var html = await HtmlDocumentBuilder.BuildAsync(root, orderedDocuments, pipeline, warnings, options.SkipMermaidInstall);

            if (options.KeepHtml)
            {
                var htmlPath = Path.ChangeExtension(output, ".html");
                await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8);
                Console.WriteLine($"Wrote intermediate HTML: {htmlPath}");
            }

            await PdfRenderer.RenderAsync(html, output);

            Console.WriteLine($"Wrote PDF: {output}");
            Console.WriteLine($"Included Markdown documents: {orderedDocuments.Count}");
            foreach (var warning in warnings.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Warning: {warning}");
            }

            return 0;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static string? FindEntryFile(string root)
    {
        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly)
            .ToDictionary(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in EntryCandidates)
        {
            if (files.TryGetValue(candidate, out var path))
            {
                return Path.GetFullPath(path);
            }
        }

        return null;
    }

    private static string ResolveRequiredFile(string root, string entryFile)
    {
        var candidate = Path.GetFullPath(Path.IsPathRooted(entryFile) ? entryFile : Path.Combine(root, entryFile));
        if (!File.Exists(candidate))
        {
            throw new ArgumentException($"Entry file does not exist: {candidate}");
        }

        return candidate;
    }

    private static string DefaultOutputPath(string root)
    {
        var directoryName = new DirectoryInfo(root).Name;
        if (string.IsNullOrWhiteSpace(directoryName))
        {
            directoryName = "document";
        }

        return Path.Combine(Environment.CurrentDirectory, $"{directoryName}.pdf");
    }

    private static bool IsExternalUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is not "file";
    }

    private static string StripUrlDecorations(string url)
    {
        url = url.Trim();
        if (url.StartsWith('<') && url.EndsWith('>'))
        {
            url = url[1..^1];
        }

        var hashIndex = url.IndexOf('#');
        if (hashIndex >= 0)
        {
            url = url[..hashIndex];
        }

        var queryIndex = url.IndexOf('?');
        if (queryIndex >= 0)
        {
            url = url[..queryIndex];
        }

        return Uri.UnescapeDataString(url);
    }

    private static string ResolveLocalPath(string currentDirectory, string url)
    {
        var localPath = StripUrlDecorations(url);
        if (Uri.TryCreate(localPath, UriKind.Absolute, out var uri) && uri.Scheme == "file")
        {
            return Path.GetFullPath(uri.LocalPath);
        }

        return Path.GetFullPath(Path.IsPathRooted(localPath) ? localPath : Path.Combine(currentDirectory, localPath));
    }

    private static bool IsUnderRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." || (!relative.StartsWith("..") && !Path.IsPathRooted(relative));
    }

    private static string RelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string DocumentId(string path)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return "doc-" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private sealed record CliOptions(
        string InputDirectory,
        string? OutputPdf,
        string? EntryFile,
        bool KeepHtml,
        bool SkipMermaidInstall,
        bool ShowHelp)
    {
        public const string Usage = """
            Usage:
              md2pdf <directory> [output.pdf] [options]

            Options:
              --entry <file>              Use a specific Markdown entry file instead of default.md/readme.md/index.md.
              --output <file>             Output PDF path. A second positional argument is also accepted.
              --keep-html                 Write the intermediate HTML next to the PDF.
              --skip-mermaid-install      Do not run npm install for Mermaid; use the CDN fallback instead.
              -h, --help                  Show this help.

            Example:
              dotnet run -- ./docs ./manual.pdf --keep-html
            """;

        public static CliOptions Parse(string[] args)
        {
            if (args.Length == 0)
            {
                return new CliOptions(".", null, null, false, false, true);
            }

            var positional = new List<string>();
            string? entry = null;
            string? output = null;
            var keepHtml = false;
            var skipMermaidInstall = false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        return new CliOptions(".", null, null, false, false, true);
                    case "--entry":
                        entry = RequireValue(args, ref i, arg);
                        break;
                    case "--output":
                        output = RequireValue(args, ref i, arg);
                        break;
                    case "--keep-html":
                        keepHtml = true;
                        break;
                    case "--skip-mermaid-install":
                        skipMermaidInstall = true;
                        break;
                    default:
                        if (arg.StartsWith('-'))
                        {
                            throw new ArgumentException($"Unknown option: {arg}");
                        }

                        positional.Add(arg);
                        break;
                }
            }

            if (positional.Count > 2)
            {
                throw new ArgumentException("Too many positional arguments.");
            }

            return new CliOptions(
                positional.Count > 0 ? positional[0] : ".",
                output ?? (positional.Count > 1 ? positional[1] : null),
                entry,
                keepHtml,
                skipMermaidInstall,
                false);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            index++;
            return args[index];
        }
    }

    private sealed record MarkdownDocumentItem(string Path, string Id, string RelativePath);

    private static class DocumentGraph
    {
        public static IReadOnlyList<MarkdownDocumentItem> Build(
            string root,
            string entryPath,
            MarkdownPipeline pipeline,
            List<string> warnings)
        {
            var ordered = new List<MarkdownDocumentItem>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Visit(entryPath);
            return ordered;

            void Visit(string path)
            {
                path = Path.GetFullPath(path);
                if (!visited.Add(path))
                {
                    return;
                }

                ordered.Add(new MarkdownDocumentItem(path, DocumentId(path), RelativePath(root, path)));

                foreach (var reference in ExtractMarkdownReferences(root, path, pipeline, warnings))
                {
                    Visit(reference);
                }
            }
        }

        private static IEnumerable<string> ExtractMarkdownReferences(
            string root,
            string sourcePath,
            MarkdownPipeline pipeline,
            List<string> warnings)
        {
            var markdown = File.ReadAllText(sourcePath);
            var document = Markdown.Parse(markdown, pipeline);
            var currentDirectory = Path.GetDirectoryName(sourcePath)!;

            foreach (var link in document.Descendants<LinkInline>())
            {
                var url = link.Url;
                if (string.IsNullOrWhiteSpace(url) || IsExternalUrl(url))
                {
                    continue;
                }

                var referencePath = ResolveLocalPath(currentDirectory, url);
                if (!MarkdownExtensions.Contains(Path.GetExtension(referencePath)))
                {
                    continue;
                }

                if (!IsUnderRoot(root, referencePath))
                {
                    warnings.Add($"{RelativePath(root, sourcePath)} references a Markdown file outside the input root: {url}");
                    continue;
                }

                if (!File.Exists(referencePath))
                {
                    warnings.Add($"{RelativePath(root, sourcePath)} references a missing Markdown file: {url}");
                    continue;
                }

                yield return referencePath;
            }
        }
    }

    private static class HtmlDocumentBuilder
    {
        public static async Task<string> BuildAsync(
            string root,
            IReadOnlyList<MarkdownDocumentItem> documents,
            MarkdownPipeline pipeline,
            List<string> warnings,
            bool skipMermaidInstall)
        {
            var documentIds = documents.ToDictionary(d => d.Path, d => d.Id, StringComparer.OrdinalIgnoreCase);
            var articles = new StringBuilder();
            var needsMermaid = false;

            foreach (var document in documents)
            {
                var markdown = await File.ReadAllTextAsync(document.Path, Encoding.UTF8);
                markdown = ExpandMermaidFileReferences(root, document.Path, markdown, warnings);

                var parsed = Markdown.Parse(markdown, pipeline);
                RewriteLocalLinks(root, document.Path, parsed, documentIds, warnings);

                var body = Markdown.ToHtml(parsed, pipeline);
                needsMermaid |= body.Contains("class=\"mermaid\"", StringComparison.OrdinalIgnoreCase)
                    || body.Contains("language-mermaid", StringComparison.OrdinalIgnoreCase);

                articles.AppendLine($"""<article id="{document.Id}" class="doc-section" data-source="{WebUtility.HtmlEncode(document.RelativePath)}">""");
                if (!HasTopLevelHeading(parsed))
                {
                    articles.AppendLine($"<h1>{WebUtility.HtmlEncode(TitleFromPath(document.Path))}</h1>");
                }

                articles.AppendLine(body);
                articles.AppendLine("</article>");
            }

            var mermaidScript = needsMermaid ? await MermaidSupport.ScriptTagAsync(skipMermaidInstall, warnings) : string.Empty;
            var mermaidRunner = needsMermaid ? MermaidSupport.RunnerScript : "window.__md2pdfReady = true;";
            var title = WebUtility.HtmlEncode(new DirectoryInfo(root).Name);

            return $$"""
                <!doctype html>
                <html>
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>{{title}}</title>
                  <style>
                {{Styles}}
                  </style>
                  {{mermaidScript}}
                </head>
                <body>
                  <main>
                {{articles}}
                  </main>
                  <script>
                {{mermaidRunner}}
                  </script>
                </body>
                </html>
                """;
        }

        private static string ExpandMermaidFileReferences(string root, string sourcePath, string markdown, List<string> warnings)
        {
            var currentDirectory = Path.GetDirectoryName(sourcePath)!;
            var output = new StringBuilder();
            var inFence = false;
            var fenceMarker = string.Empty;

            foreach (var rawLine in ReadLines(markdown))
            {
                var line = rawLine;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
                {
                    var marker = trimmed[..3];
                    if (!inFence)
                    {
                        inFence = true;
                        fenceMarker = marker;
                    }
                    else if (marker == fenceMarker)
                    {
                        inFence = false;
                    }
                }

                if (!inFence)
                {
                    line = MarkdownLinkRegex.Replace(line, match =>
                    {
                        var url = match.Groups["url"].Value;
                        if (IsExternalUrl(url))
                        {
                            return match.Value;
                        }

                        var referencePath = ResolveLocalPath(currentDirectory, url);
                        if (!MermaidExtensions.Contains(Path.GetExtension(referencePath)))
                        {
                            return match.Value;
                        }

                        if (!IsUnderRoot(root, referencePath))
                        {
                            warnings.Add($"{RelativePath(root, sourcePath)} references a Mermaid file outside the input root: {url}");
                            return match.Value;
                        }

                        if (!File.Exists(referencePath))
                        {
                            warnings.Add($"{RelativePath(root, sourcePath)} references a missing Mermaid file: {url}");
                            return match.Value;
                        }

                        var mermaid = File.ReadAllText(referencePath);
                        var caption = match.Groups["text"].Value;
                        var captionHtml = string.IsNullOrWhiteSpace(caption)
                            ? string.Empty
                            : $"<figcaption>{WebUtility.HtmlEncode(caption)}</figcaption>";

                        return $"""

                            <figure class="diagram">
                            <div class="mermaid">
                            {WebUtility.HtmlEncode(mermaid)}
                            </div>
                            {captionHtml}
                            </figure>

                            """;
                    });
                }

                output.AppendLine(line);
            }

            return output.ToString();
        }

        private static IEnumerable<string> ReadLines(string value)
        {
            using var reader = new StringReader(value);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                yield return line;
            }
        }

        private static void RewriteLocalLinks(
            string root,
            string sourcePath,
            MarkdownDocument parsed,
            IReadOnlyDictionary<string, string> documentIds,
            List<string> warnings)
        {
            var currentDirectory = Path.GetDirectoryName(sourcePath)!;

            foreach (var link in parsed.Descendants<LinkInline>())
            {
                var url = link.Url;
                if (string.IsNullOrWhiteSpace(url) || IsExternalUrl(url))
                {
                    continue;
                }

                var referencePath = ResolveLocalPath(currentDirectory, url);
                var extension = Path.GetExtension(referencePath);

                if (MarkdownExtensions.Contains(extension))
                {
                    if (documentIds.TryGetValue(referencePath, out var documentId))
                    {
                        link.Url = "#" + documentId;
                    }

                    continue;
                }

                if (!IsUnderRoot(root, referencePath))
                {
                    warnings.Add($"{RelativePath(root, sourcePath)} references a file outside the input root: {url}");
                    continue;
                }

                if (!File.Exists(referencePath))
                {
                    warnings.Add($"{RelativePath(root, sourcePath)} references a missing file: {url}");
                    continue;
                }

                if (ImageExtensions.Contains(extension) || link.IsImage)
                {
                    link.Url = new Uri(referencePath).AbsoluteUri;
                }
            }
        }

        private static bool HasTopLevelHeading(MarkdownDocument document) =>
            document.OfType<HeadingBlock>().Any(heading => heading.Level == 1);

        private static string TitleFromPath(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path).Replace('-', ' ').Replace('_', ' ');
            return Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(name);
        }
    }

    private static class MermaidSupport
    {
        public const string RunnerScript = """
            (async () => {
              try {
                document.querySelectorAll('pre > code.language-mermaid').forEach((code) => {
                  const container = document.createElement('div');
                  container.className = 'mermaid';
                  container.textContent = code.textContent;
                  code.parentElement.replaceWith(container);
                });

                if (document.querySelector('.mermaid')) {
                  mermaid.initialize({ startOnLoad: false, securityLevel: 'loose', theme: 'default' });
                  await mermaid.run({ querySelector: '.mermaid' });
                }

                window.__md2pdfReady = true;
              } catch (error) {
                document.body.insertAdjacentHTML(
                  'beforeend',
                  '<pre class="render-error">Mermaid render failed: ' + String(error).replace(/[<>&]/g, '') + '</pre>');
                window.__md2pdfReady = true;
              }
            })();
            """;

        public static async Task<string> ScriptTagAsync(bool skipInstall, List<string> warnings)
        {
            if (!skipInstall)
            {
                try
                {
                    var scriptPath = await EnsureLocalMermaidAsync();
                    var script = await File.ReadAllTextAsync(scriptPath, Encoding.UTF8);
                    return "<script>" + script + "</script>";
                }
                catch (Exception ex)
                {
                    warnings.Add($"Could not install/load local Mermaid package, falling back to CDN: {ex.Message}");
                }
            }

            return """<script src="https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js"></script>""";
        }

        private static async Task<string> EnsureLocalMermaidAsync()
        {
            var cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Md2Pdf",
                "node-tools");
            var scriptPath = Path.Combine(cacheRoot, "node_modules", "mermaid", "dist", "mermaid.min.js");
            if (File.Exists(scriptPath))
            {
                return scriptPath;
            }

            Directory.CreateDirectory(cacheRoot);
            await RunNpmAsync($"install --prefix {QuoteArgument(cacheRoot)} mermaid@11", Environment.CurrentDirectory);

            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("Mermaid package did not contain dist/mermaid.min.js.", scriptPath);
            }

            return scriptPath;
        }

        private static async Task RunNpmAsync(string arguments, string workingDirectory)
        {
            if (OperatingSystem.IsWindows() && TryFindOnPath("npm.cmd", out var npmCmdPath))
            {
                var nodeDirectory = Path.GetDirectoryName(npmCmdPath)!;
                var nodePath = Path.Combine(nodeDirectory, "node.exe");
                var npmCliPath = Path.Combine(nodeDirectory, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(nodePath) && File.Exists(npmCliPath))
                {
                    await RunProcessAsync(nodePath, $"{QuoteArgument(npmCliPath)} {arguments}", workingDirectory);
                    return;
                }
            }

            await RunProcessAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", arguments, workingDirectory);
        }

        private static bool TryFindOnPath(string executableName, out string path)
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Path.Combine(directory, executableName);
                if (File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }

            path = string.Empty;
            return false;
        }
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static class PdfRenderer
    {
        public static async Task RenderAsync(string html, string outputPath)
        {
            using var playwright = await CreatePlaywrightAsync();
            await using var browser = await LaunchChromiumAsync(playwright);
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = 120_000
            });
            await page.WaitForFunctionAsync("() => window.__md2pdfReady === true", null, new PageWaitForFunctionOptions
            {
                Timeout = 120_000
            });
            await page.EmulateMediaAsync(new PageEmulateMediaOptions
            {
                Media = Media.Print
            });
            await page.PdfAsync(new PagePdfOptions
            {
                Path = outputPath,
                Format = "A4",
                PrintBackground = true,
                PreferCSSPageSize = true
            });
        }

        private static async Task<IPlaywright> CreatePlaywrightAsync()
        {
            return await Playwright.CreateAsync();
        }

        private static async Task<IBrowser> LaunchChromiumAsync(IPlaywright playwright)
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });
            }
            catch (PlaywrightException ex) when (ex.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
            {
                InstallPlaywrightChromium();
                return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = true
                });
            }
        }

        private static void InstallPlaywrightChromium()
        {
            var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Playwright browser installation failed with exit code {exitCode}.");
            }
        }
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            throw new InvalidOperationException($"{fileName} {arguments} failed with exit code {process.ExitCode}: {stdout}{stderr}");
        }
    }

    private const string Styles = """
        @page {
          size: A4;
          margin: 18mm 16mm;
        }

        * {
          box-sizing: border-box;
        }

        html {
          color: #1f2937;
          font-family: "Segoe UI", Arial, sans-serif;
          font-size: 11pt;
          line-height: 1.55;
        }

        body {
          margin: 0;
          background: #ffffff;
        }

        main {
          width: 100%;
        }

        .doc-section {
          break-before: page;
          page-break-before: always;
        }

        .doc-section:first-child {
          break-before: auto;
          page-break-before: auto;
        }

        h1,
        h2,
        h3,
        h4 {
          color: #111827;
          line-height: 1.2;
          margin: 1.45em 0 0.55em;
          page-break-after: avoid;
        }

        h1 {
          border-bottom: 2px solid #d1d5db;
          font-size: 24pt;
          margin-top: 0;
          padding-bottom: 0.18in;
        }

        h2 {
          border-bottom: 1px solid #e5e7eb;
          font-size: 17pt;
          padding-bottom: 0.08in;
        }

        h3 {
          font-size: 13.5pt;
        }

        p,
        ul,
        ol,
        table,
        blockquote,
        pre,
        figure {
          margin: 0.75em 0;
        }

        a {
          color: #0f766e;
          text-decoration: none;
        }

        img {
          display: block;
          height: auto;
          max-width: 100%;
        }

        figure {
          break-inside: avoid;
          page-break-inside: avoid;
        }

        figcaption {
          color: #4b5563;
          font-size: 9.5pt;
          margin-top: 0.3em;
          text-align: center;
        }

        blockquote {
          border-left: 4px solid #94a3b8;
          color: #374151;
          margin-left: 0;
          padding: 0.2em 0 0.2em 1em;
        }

        pre,
        code {
          font-family: "Cascadia Mono", Consolas, "Courier New", monospace;
        }

        code {
          background: #eef2f7;
          border-radius: 4px;
          font-size: 0.92em;
          padding: 0.08em 0.28em;
        }

        pre {
          background: #ffffff;
          border: 1px solid #d1d5db;
          border-radius: 6px;
          color: #111827;
          line-height: 1.45;
          overflow: hidden;
          padding: 0.85em 1em;
          white-space: pre-wrap;
          word-break: break-word;
        }

        pre code {
          background: transparent;
          border-radius: 0;
          color: inherit;
          display: block;
          font-size: 9.5pt;
          padding: 0;
        }

        table {
          border-collapse: collapse;
          width: 100%;
        }

        th,
        td {
          border: 1px solid #d1d5db;
          padding: 0.4em 0.55em;
          vertical-align: top;
        }

        th {
          background: #f3f4f6;
          color: #111827;
          font-weight: 600;
        }

        .diagram {
          align-items: center;
          display: flex;
          flex-direction: column;
          gap: 0.2em;
          margin: 1em 0;
          max-width: 100%;
        }

        .mermaid {
          background: #ffffff;
          border: 1px solid #d1d5db;
          border-radius: 6px;
          padding: 0.75em;
          max-width: 100%;
          text-align: center;
        }

        .mermaid svg {
          height: auto !important;
          max-width: 100% !important;
        }

        .render-error {
          background: #fee2e2;
          color: #991b1b;
        }
        """;
}
