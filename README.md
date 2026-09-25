# Md2Pdf

Md2Pdf is a small C# command-line application that converts a directory of linked Markdown documents into a single PDF.

The tool starts from a main Markdown file, renders that file first, discovers referenced Markdown files in link order, recursively follows those references, and appends each discovered document as a new PDF section.

## How It Works

1. The application resolves the input directory.
2. It chooses the entry Markdown file:
   - `default.md`
   - `readme.md`
   - `index.md`
   - or a file passed with `--entry`
3. It parses the entry file with Markdig.
4. It scans local Markdown links in the order they appear in the source document.
5. For each local Markdown reference, it:
   - validates that the file exists
   - validates that the file is inside the input directory
   - adds it to the output order if it has not already been included
   - recursively scans that document for more Markdown references
6. It renders all included Markdown files into one HTML document.
7. It rewrites included Markdown links to internal document anchors.
8. It rewrites local image paths to `file://` URLs so Chromium can render them.
9. It renders Mermaid diagrams before printing.
10. It uses Playwright with Chromium to print the final HTML as a PDF.

Each included Markdown file starts on a new PDF page.

## Supported Input

The converter supports:

- Markdown files with `.md` or `.markdown` extensions.
- Local Markdown links, for example:

```markdown
[Analytics](docs/analytics.md)
```

- Recursive Markdown references.
- Fenced code blocks:

````markdown
```csharp
Console.WriteLine("Hello");
```
````

- Local image references:

```markdown
![Diagram](images/diagram.png)
```

- Mermaid fenced blocks:

````markdown
```mermaid
flowchart TD
  A --> B
```
````

- Linked Mermaid files:

```markdown
[Architecture](diagrams/architecture.mmd)
```

Supported local image extensions are:

- `.png`
- `.jpg`
- `.jpeg`
- `.gif`
- `.bmp`
- `.webp`
- `.svg`

## Usage

```powershell
dotnet run -- <directory> [output.pdf] [options]
```

Options:

```text
--entry <file>              Use a specific Markdown entry file instead of default.md/readme.md/index.md.
--output <file>             Output PDF path. A second positional argument is also accepted.
--keep-html                 Write the intermediate HTML next to the PDF.
--skip-mermaid-install      Do not run npm install for Mermaid; use the CDN fallback instead.
-h, --help                  Show help.
```

Example:

```powershell
dotnet run -- C:\Projects\TaskBeat --entry C:\Projects\TaskBeat\readme.md --output .\output\pdf\taskbeat.pdf --keep-html
```

Published executable example:

```powershell
Md2Pdf.exe C:\Projects\TaskBeat --entry C:\Projects\TaskBeat\readme.md --output C:\Temp\taskbeat.pdf
```

## Deployment Dependencies

Install these dependencies on the target machine before deploying or running the application.

### Required

- .NET 10 Runtime, or .NET 10 SDK if running with `dotnet run`.
- Network access during first setup, unless all NuGet packages, Playwright browsers, and Mermaid assets are preinstalled.
- A user profile with write access to local application data, because Mermaid is cached under:

```text
%LOCALAPPDATA%\Md2Pdf\node-tools
```

### Required For Build

- .NET 10 SDK.
- NuGet package restore access.

NuGet packages used by the project:

- `Markdig`
- `Microsoft.Playwright`

### Required For PDF Rendering

- Playwright-compatible Chromium.

The application attempts to install Chromium automatically by calling Playwright's browser installer if the browser executable is missing. In locked-down deployment environments, preinstall the browser during packaging or release setup instead of relying on runtime installation.

### Required For Mermaid Rendering

- Node.js with npm.

The application installs Mermaid into:

```text
%LOCALAPPDATA%\Md2Pdf\node-tools
```

If npm or local Mermaid installation fails, the generated HTML falls back to the Mermaid CDN:

```text
https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.min.js
```

For offline deployments, install Node.js and allow the first run to populate the Mermaid cache, or package the cache with the deployment image.

## Deployment Notes

Build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

Self-contained deployment:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
```

Recommended deployment checklist:

- Install or bundle the .NET runtime unless publishing self-contained.
- Restore NuGet packages during build.
- Run Playwright browser installation during deployment if runtime downloads are not allowed.
- Install Node.js and npm if Mermaid diagrams are expected.
- Confirm the process identity can read the input documentation directory.
- Confirm the process identity can write the output PDF path.
- Confirm the process identity can write to `%LOCALAPPDATA%\Md2Pdf`.

## Exclusions

Md2Pdf does not:

- Crawl every Markdown file in the directory automatically.
- Include unreferenced Markdown files.
- Fetch missing files from GitHub, wikis, HTTP URLs, or other remote systems.
- Convert external web pages into PDF sections.
- Execute code blocks.
- Evaluate custom Markdown include directives.
- Render application-specific macros unless Markdig already supports them.
- Generate a table of contents.
- Add page numbers, headers, or footers.
- Apply syntax highlighting themes beyond the built-in code block styling.
- Validate Mermaid semantics before rendering.

## Limitations

- Reference discovery is based on Markdown links. Plain-text file paths are not treated as references.
- Local Markdown links are only included when they point to `.md` or `.markdown` files.
- Files outside the input directory are skipped and reported as warnings.
- Missing local references are reported as warnings; they do not stop PDF generation.
- Cyclic references are handled by including each Markdown file only once.
- The first discovered reference determines where a document appears in the PDF.
- Anchor fragments and query strings are ignored when resolving local files.
- External links remain external links.
- Mermaid rendering depends on Chromium and Mermaid support for the diagram syntax used.
- Very large document sets can produce large intermediate HTML and high Chromium memory usage.
- Browser print layout can differ from GitHub's Markdown rendering.
- PDF text extraction may not preserve exact spacing even when the visual PDF is correct.

## Output

The application writes:

- the requested PDF file
- an optional intermediate HTML file when `--keep-html` is used
- warnings to standard output

Example warning:

```text
Warning: docs/deployment.md references a missing Markdown file: wiki\OpenAI.md
```

Warnings are intended to make incomplete documentation sets visible without blocking successful PDF creation.
