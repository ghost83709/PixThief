using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PixThief;

class Program
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options == null || string.IsNullOrEmpty(options.Url))
            {
                // ParseArguments already printed an error or help message.
                return 1;
            }

            var scraper = new ImageScraper(options);

            if (options.IsDomainMode)
            {
                await scraper.DownloadImagesForDomainAsync();
            }
            else
            {
                await scraper.DownloadImagesForSinglePageAsync();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] {ex.Message}");
            return 1;
        }
    }

    static ScraperOptions? ParseArguments(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return null;
        }

        // Allow calling with just --help / -h / /? without a URL
        if (IsHelpOption(args[0]))
        {
            PrintUsage();
            return null;
        }

        // First argument is always the URL
        var url = args[0];

        // Validate URL format
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            Console.Error.WriteLine("[ERROR] Invalid URL. Must be a valid HTTP or HTTPS URL.");
            return null;
        }

        var options = new ScraperOptions { Url = url };

        // Parse optional arguments
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];

            switch (arg.ToLowerInvariant())
            {
                case "--domain":
                    options.IsDomainMode = true;
                    break;

                case "--include-gifs":
                    options.IncludeAnimatedGifs = true;
                    break;

                case "--stealth":
                    options.StealthMode = true;
                    options.RandomizeDelays = true;
                    break;

                case "--delay":
                    {
                        if (!TryReadIntOption(args, ref i, "--delay", out var delay))
                            return null;

                        if (delay <= 0)
                        {
                            Console.Error.WriteLine("[ERROR] --delay must be a positive number of milliseconds.");
                            return null;
                        }

                        options.RequestDelayMs = delay;
                        options.StealthMode = true;
                        options.RandomizeDelays = false;
                        break;
                    }

                case "--max-pages":
                    {
                        if (!TryReadIntOption(args, ref i, "--max-pages", out var maxPages))
                            return null;

                        if (maxPages <= 0)
                        {
                            Console.Error.WriteLine("[ERROR] --max-pages must be a positive integer.");
                            return null;
                        }

                        options.MaxPages = maxPages;
                        break;
                    }

                case "--out":
                    {
                        if (!TryReadValueOption(args, ref i, "--out", out var folder))
                            return null;

                        options.OutputFolder = SanitizeFileName(folder);
                        break;
                    }

                case "--convert-to":
                    {
                        if (!TryReadValueOption(args, ref i, "--convert-to", out var formatRaw))
                            return null;

                        var format = formatRaw.ToLowerInvariant();
                        if (format == "jpeg")
                            format = "jpg";

                        if (format != "jpg" && format != "png" && format != "gif")
                        {
                            Console.Error.WriteLine("[ERROR] Invalid value for --convert-to. Use: jpg, png, or gif.");
                            return null;
                        }

                        options.ConvertToFormat = format;
                        break;
                    }

                case "--jpeg-quality":
                    {
                        if (!TryReadIntOption(args, ref i, "--jpeg-quality", out var quality))
                            return null;

                        if (quality < 1 || quality > 100)
                        {
                            Console.Error.WriteLine("[ERROR] --jpeg-quality must be between 1 and 100.");
                            return null;
                        }

                        options.JpegQuality = quality;
                        break;
                    }

                case "--verbose":
                    options.IsVerbose = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    PrintUsage();
                    return null;

                default:
                    Console.Error.WriteLine($"[ERROR] Unknown option: {arg}");
                    return null;
            }
        }

        return options;
    }

    static bool IsHelpOption(string arg)
    {
        var value = arg.ToLowerInvariant();
        return value == "--help" || value == "-h" || value == "/?";
    }

    static bool TryReadValueOption(string[] args, ref int index, string optionName, out string value)
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"[ERROR] {optionName} requires a value.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    static bool TryReadIntOption(string[] args, ref int index, string optionName, out int value)
    {
        value = 0;

        if (!TryReadValueOption(args, ref index, optionName, out var raw))
            return false;

        if (!int.TryParse(raw, out value))
        {
            Console.Error.WriteLine($"[ERROR] Invalid value for {optionName}. Must be an integer.");
            return false;
        }

        return true;
    }

    static void PrintUsage()
    {
        Console.WriteLine("PixThief - Steal images from web pages");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  PixThief.exe <url> [options]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  <url>              URL of the web page to scrape (HTTP/HTTPS required)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --domain           Crawl the entire domain, not just the given page");
        Console.WriteLine("  --max-pages <n>    Max pages to crawl in domain mode (default: 100)");
        Console.WriteLine("  --out <name>       Override the output folder name");
        Console.WriteLine("  --include-gifs     Include animated GIF files in downloads");
        Console.WriteLine("  --stealth          Enable stealth mode with smart randomized delays");
        Console.WriteLine("  --delay <ms>       Use a fixed delay between requests (disables randomization)");
        Console.WriteLine("  --convert-to <fmt> Convert all images to: jpg, png, or gif");
        Console.WriteLine("  --jpeg-quality <n> JPEG quality for conversion (1-100, default: 90)");
        Console.WriteLine("  --verbose          Print detailed information about operations");
        Console.WriteLine("  --help, -h         Show this help message and exit");
        Console.WriteLine();
        Console.WriteLine("Stealth mode:");
        Console.WriteLine("  - Uses realistic HTTP headers and randomized delays");
        Console.WriteLine("  - Applies progressive backoff when rate limiting is detected");
        Console.WriteLine("  - Aims to mimic human browsing patterns");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  PixThief.exe https://example.com/page");
        Console.WriteLine("  PixThief.exe https://example.com --domain --stealth");
        Console.WriteLine("  PixThief.exe https://example.com --stealth --max-pages 50");
        Console.WriteLine("  PixThief.exe https://example.com --delay 2000 --convert-to jpg");
    }

    static string SanitizeFileName(string fileName)
    {
        var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var invalidRegex = new Regex($"[{invalidChars}]");
        return invalidRegex.Replace(fileName, "_");
    }
}
