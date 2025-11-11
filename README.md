# PixThief

PixThief is a .NET 8 console application that crawls a single page or an entire domain and downloads all images it can find. 
It supports stealthy crawling, smart delays, and optional image format conversion.

## Features

- Single-page mode or whole-domain crawling (BFS).
- Multiple image discovery strategies:
  - `<img>` and `<picture>` tags
  - CSS background images
  - `data-*` attributes
  - Media posters and inline styles
- Optional inclusion of animated GIFs.
- Stealth mode with realistic HTTP headers and human-like random delays.
- Automatic retry and backoff when rate-limited (HTTP 429).
- Optional image format conversion (JPG/PNG/GIF) using ImageSharp.
- Configurable JPEG quality for converted images.
- Deduplicated downloads (each image URL is fetched at most once).

## Requirements

- .NET 8 SDK
- Windows x64 (project is configured with `win-x64` as runtime identifier)

## Building

Clone the repository and build from the solution:

```bash
dotnet build getpics.sln -c Release
```

Or build the project directly:

```bash
dotnet build getpics/getpics.csproj -c Release
```

### Publishing a single-file, self-contained binary (Release)

```bash
dotnet publish getpics/getpics.csproj -c Release -r win-x64 /p:PublishSingleFile=true /p:PublishTrimmed=true /p:SelfContained=true
```

The published binary will be in:

```text
getpics/bin/Release/net8.0/win-x64/publish/
```

## Usage

Basic syntax:

```text
PixThief.exe <url> [options]
```

Example (single page):

```bash
PixThief.exe https://example.com
```

Example (crawl entire domain with stealth mode and 2-second base delay):

```bash
PixThief.exe https://example.com --domain --stealth --delay 2000
```

Example (convert everything to JPEG with quality 85):

```bash
PixThief.exe https://example.com/gallery --convert-to jpg --jpeg-quality 85
```

## Options

- `--domain`  
  Crawl the entire domain instead of just the single page.

- `--out <folder>`  
  Set a custom output folder name. Invalid characters are sanitized.

- `--max-pages <n>`  
  Maximum number of pages to crawl in domain mode (default: 100). Must be > 0.

- `--include-gifs`  
  Include animated GIFs in the download set.

- `--stealth`  
  Enable stealth mode with realistic browser headers and randomized delays.

- `--delay <ms>`  
  Set a base delay in milliseconds between requests (must be > 0).  
  When used without `--stealth`, this is a fixed delay.  
  In stealth mode, this is the base for randomized delays.

- `--convert-to <fmt>`  
  Convert downloaded images to a specific format. Supported values: `jpg`, `jpeg`, `png`, `gif`.

- `--jpeg-quality <1-100>`  
  JPEG encoder quality to use when `--convert-to jpg` is set (default: 90).

- `--verbose`  
  Show detailed error and debug output.

- `--help`, `-h`, `/?`  
  Show usage information and exit.

## Notes

PixThief does not enforce any particular crawling policy. How you use this tool is your responsibility. 
Make sure you understand and respect the laws and terms that apply in your jurisdiction and to the sites you interact with.

## License

This project is licensed under the MIT License. See the [LICENSE](LICENSE) file for details.
