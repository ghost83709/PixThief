using AngleSharp;
using AngleSharp.Dom;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Gif;

namespace PixThief;

class ImageScraper
{
    // Default image extensions (excluding .gif by default)
    private static readonly string[] ImageExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".svg", ".bmp", ".ico" };
    private static readonly string[] AllImageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".bmp", ".ico" };
    private static readonly HashSet<string> VisitedUrls = new();
    private static readonly HashSet<string> DownloadedImages = new();
    private readonly ScraperOptions _options;
    private readonly HttpClient _httpClient;
    private readonly IBrowsingContext _browsingContext;
    private readonly Random _random; // For randomized delays
    private string? _outputFolder;
    private int _totalImagesFound = 0;
    private int _consecutiveErrors = 0; // Track errors for backoff
    private int _currentBackoffMultiplier = 1; // Progressive delay increase

    public ImageScraper(ScraperOptions options)
    {
        _options = options;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });
        _random = new Random();
        
        // Stealth mode: add realistic request headers and randomization
        if (options.StealthMode)
        {
            AddStealthHeaders();
        }
        else
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", 
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        }
        
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _browsingContext = BrowsingContext.New(AngleSharp.Configuration.Default);
    }

    private void AddStealthHeaders()
    {
        // Rotate through realistic user agents to avoid detection
        var userAgents = new[]
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:121.0) Gecko/20100101 Firefox/121.0",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36 Edg/121.0.0.0",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
        };

        var userAgent = userAgents[_random.Next(userAgents.Length)];
        
        _httpClient.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8");
        _httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        // REMOVED Accept-Encoding - let HttpClient handle it automatically
        _httpClient.DefaultRequestHeaders.Add("DNT", "1");
        _httpClient.DefaultRequestHeaders.Add("Connection", "keep-alive");
        _httpClient.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
    }

    private int GetRandomizedDelay(int baseDelayMs)
    {
        // Only randomize if RandomizeDelay is true (for --stealth only)
        if (_options.RandomizeDelays)
        {
            // Research-backed safe randomization for web scraping:
            // - Too predictable: Easily detected as bot (±10%)
            // - Too random: Unrealistic human behavior (±50%+)
            // - Optimal range: ±20-40% mimics human variance
            // 
            // Studies show humans have 25-35% natural variance in timing.
            // We use ±40% to be extra safe and unpredictable.
            //
            // Additional safety: Use Normal distribution (bell curve) instead of uniform
            // Real humans cluster around average with occasional outliers
            
            var variance = (int)(baseDelayMs * 0.4); // ±40% variance
            
            // Generate bell curve distribution (more human-like)
            // Most delays near center, some outliers
            var random1 = _random.NextDouble();
            var random2 = _random.NextDouble();
            var gaussian = Math.Sqrt(-2.0 * Math.Log(random1)) * Math.Cos(2.0 * Math.PI * random2);
            
            // Scale Gaussian (mean=0, stddev=1) to our variance range
            // Clamp to ±2 standard deviations (95% of values)
            var scaledVariance = (int)(gaussian * variance / 2);
            scaledVariance = Math.Clamp(scaledVariance, -variance, variance);
            
            var finalDelay = baseDelayMs + scaledVariance;
            
            // Safety: Never go below 200ms (too fast = suspicious)
            return Math.Max(200, finalDelay);
        }
        
        // Fixed delay for --delay flag
        return baseDelayMs;
    }

    public async Task DownloadImagesForSinglePageAsync()
    {
        if (string.IsNullOrEmpty(_options.Url))
            throw new ArgumentException("URL is required");

        Console.WriteLine("Starting single-page image download...");
        if (_options.StealthMode)
        {
            var delayType = _options.RandomizeDelays ? "randomized" : "fixed";
            Console.WriteLine($"Stealth mode enabled with {_options.RequestDelayMs}ms {delayType} delay between downloads");
        }
        
        if (!string.IsNullOrEmpty(_options.ConvertToFormat))
        {
            Console.WriteLine($"Converting all images to {_options.ConvertToFormat.ToUpper()} during download");
        }
        
        _outputFolder = DetermineOutputFolder(_options.Url);
        Directory.CreateDirectory(_outputFolder);

        await ExtractAndDownloadImagesFromPageAsync(_options.Url);
        Console.WriteLine($"Download complete. Total images found: {_totalImagesFound}");
    }

    public async Task DownloadImagesForDomainAsync()
    {
        if (string.IsNullOrEmpty(_options.Url))
            throw new ArgumentException("URL is required");

        Console.WriteLine("Starting domain-wide crawling...");
        if (_options.StealthMode)
        {
            var delayType = _options.RandomizeDelays ? "randomized" : "fixed";
            Console.WriteLine($"Stealth mode enabled with {_options.RequestDelayMs}ms {delayType} delay between requests");
        }
        
        if (!string.IsNullOrEmpty(_options.ConvertToFormat))
        {
            Console.WriteLine($"Converting all images to {_options.ConvertToFormat.ToUpper()} during download");
        }
        
        _outputFolder = DetermineOutputFolder(_options.Url);
        Directory.CreateDirectory(_outputFolder);

        var uri = new Uri(_options.Url);
        var queue = new Queue<string>();
        queue.Enqueue(_options.Url);
        VisitedUrls.Clear();
        DownloadedImages.Clear();

        int pageCount = 0;

        while (queue.Count > 0 && pageCount < _options.MaxPages)
        {
            var pageUrl = queue.Dequeue();

            if (VisitedUrls.Contains(pageUrl))
                continue;

            VisitedUrls.Add(pageUrl);
            pageCount++;

            Console.WriteLine($"[{pageCount}/{_options.MaxPages}] Crawling: {pageUrl}");

            try
            {
                // Apply stealth mode delay (randomized for --stealth, fixed for --delay) before each request
                if (_options.StealthMode && pageCount > 1)
                {
                    var delay = GetRandomizedDelay(_options.RequestDelayMs);
                    Log($"Waiting {delay}ms before next request...");
                    await Task.Delay(delay);
                }

                // Extract images from current page
                await ExtractAndDownloadImagesFromPageAsync(pageUrl);

                // Find all links on the page
                var links = await ExtractLinksFromPageAsync(pageUrl);

                // Add unvisited same-domain links to queue
                foreach (var link in links)
                {
                    if (!VisitedUrls.Contains(link) && IsSameDomain(link, uri))
                    {
                        queue.Enqueue(link);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Error processing page {pageUrl}: {ex.Message}");
            }
        }

        Console.WriteLine($"Crawling complete. Processed {pageCount} pages. Total images found: {_totalImagesFound}");
    }

    private async Task ExtractAndDownloadImagesFromPageAsync(string pageUrl)
    {
        try
        {
            var html = await FetchHtmlAsync(pageUrl);
            if (string.IsNullOrEmpty(html))
                return;

            var document = await _browsingContext.OpenAsync(req => req.Content(html));
            var imageUrls = new HashSet<string>();

            // 1. Extract from <img> tags (src and srcset)
            ExtractFromImageTags(document, imageUrls, pageUrl);

            // 2. Extract from picture/source elements
            ExtractFromPictureElements(document, imageUrls, pageUrl);

            // 3. Extract from CSS background-image properties
            ExtractFromBackgroundImages(document, imageUrls, pageUrl);

            // 4. Extract from data attributes and custom attributes
            ExtractFromDataAttributes(document, imageUrls, pageUrl);

            // 5. Extract from <video> and <audio> poster attributes
            ExtractFromMediaElements(document, imageUrls, pageUrl);

            // 6. Extract image URLs from inline styles and attributes
            ExtractFromInlineStyles(html, imageUrls, pageUrl);

            // 7. Extract URLs from CSS and JavaScript comments (regex-based)
            ExtractFromTextContent(html, imageUrls, pageUrl);

            // Filter for valid image URLs
            var validImageUrls = imageUrls
                .Where(url => IsValidImageUrl(url))
                .Distinct()
                .ToList();

            _totalImagesFound += validImageUrls.Count;
            Console.WriteLine($"Found {validImageUrls.Count} images on this page (total so far: {_totalImagesFound})");

            // Download images
            foreach (var imageUrl in validImageUrls)
            {
                await DownloadImageAsync(imageUrl);
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Error extracting images from {pageUrl}", ex);
        }
    }

    private void ExtractFromImageTags(IDocument document, HashSet<string> imageUrls, string pageUrl)
    {
        var imageElements = document.QuerySelectorAll("img");

        foreach (var img in imageElements)
        {
            // Try srcset first (pick all sizes, not just largest)
            var srcSet = img.GetAttribute("srcset");
            if (!string.IsNullOrEmpty(srcSet))
            {
                var urls = ParseSrcSet(srcSet);
                foreach (var url in urls)
                {
                    imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
                }
            }

            // Also get src attribute
            var src = img.GetAttribute("src");
            if (!string.IsNullOrEmpty(src) && !src.StartsWith("data:"))
            {
                imageUrls.Add(ConvertToAbsoluteUrl(src, pageUrl));
            }

            // Check for alt text that might be a URL (rare but happens)
            var alt = img.GetAttribute("alt");
            if (!string.IsNullOrEmpty(alt) && IsLikelyImageUrl(alt))
            {
                imageUrls.Add(ConvertToAbsoluteUrl(alt, pageUrl));
            }
        }
    }

    private void ExtractFromPictureElements(IDocument document, HashSet<string> imageUrls, string pageUrl)
    {
        var pictures = document.QuerySelectorAll("picture");

        foreach (var picture in pictures)
        {
            // Get source elements
            var sources = picture.QuerySelectorAll("source");
            foreach (var source in sources)
            {
                var srcSet = source.GetAttribute("srcset");
                if (!string.IsNullOrEmpty(srcSet))
                {
                    var urls = ParseSrcSet(srcSet);
                    foreach (var url in urls)
                    {
                        imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
                    }
                }
            }

            // Also get img within picture
            var img = picture.QuerySelector("img");
            if (img != null)
            {
                var src = img.GetAttribute("src");
                if (!string.IsNullOrEmpty(src) && !src.StartsWith("data:"))
                {
                    imageUrls.Add(ConvertToAbsoluteUrl(src, pageUrl));
                }
            }
        }
    }

    private void ExtractFromBackgroundImages(IDocument document, HashSet<string> imageUrls, string pageUrl)
    {
        // Get all elements that might have background images
        var allElements = document.QuerySelectorAll("*");

        foreach (var element in allElements)
        {
            var style = element.GetAttribute("style");
            if (!string.IsNullOrEmpty(style))
            {
                var bgUrls = ExtractUrlsFromCss(style);
                foreach (var url in bgUrls)
                {
                    imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
                }
            }
        }
    }

    private void ExtractFromDataAttributes(IDocument document, HashSet<string> imageUrls, string pageUrl)
    {
        var elements = document.QuerySelectorAll("[data-src], [data-image], [data-background], [data-thumbnail], [data-thumb]");

        foreach (var element in elements)
        {
            // Check all data attributes
            foreach (var attr in new[] { "data-src", "data-image", "data-background", "data-thumbnail", "data-thumb", "data-lazy-src" })
            {
                var url = element.GetAttribute(attr);
                if (!string.IsNullOrEmpty(url) && IsLikelyImageUrl(url))
                {
                    imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
                }
            }
        }
    }

    private void ExtractFromMediaElements(IDocument document, HashSet<string> imageUrls, string pageUrl)
    {
        // Extract from video poster
        var videos = document.QuerySelectorAll("video");
        foreach (var video in videos)
        {
            var poster = video.GetAttribute("poster");
            if (!string.IsNullOrEmpty(poster))
            {
                imageUrls.Add(ConvertToAbsoluteUrl(poster, pageUrl));
            }
        }

        // Extract from audio covers (if any)
        var audios = document.QuerySelectorAll("audio");
        foreach (var audio in audios)
        {
            var cover = audio.GetAttribute("cover");
            if (!string.IsNullOrEmpty(cover))
            {
                imageUrls.Add(ConvertToAbsoluteUrl(cover, pageUrl));
            }
        }
    }

    private void ExtractFromInlineStyles(string html, HashSet<string> imageUrls, string pageUrl)
    {
        // Extract URLs from style tags and inline styles
        var stylePattern = @"(?:background-image|background)\s*:\s*url\(['""]?([^)'""])+['""]?\)";
        var matches = Regex.Matches(html, stylePattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                var url = match.Groups[1].Value.Trim();
                if (IsLikelyImageUrl(url))
                {
                    imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
                }
            }
        }
    }

    private void ExtractFromTextContent(string html, HashSet<string> imageUrls, string pageUrl)
    {
        // Extract image URLs from HTML text (including JSON-LD, scripts, etc.)
        // Match common image URL patterns
        var urlPattern = @"""(?:image|url|src|thumbnail|thumb|poster|bg|background)["":\s]*""?\s*:\s*""([^""]+\.(?:jpg|jpeg|png|gif|webp|svg|bmp|ico))""";
        var matches = Regex.Matches(html, urlPattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                var url = match.Groups[1].Value.Trim();
                imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
            }
        }

        // Also match protocol-relative and absolute image URLs
        var absUrlPattern = @"""?((?:https?:)?//[^\s""'<>]+\.(?:jpg|jpeg|png|gif|webp|svg|bmp|ico))""?";
        var absMatches = Regex.Matches(html, absUrlPattern, RegexOptions.IgnoreCase);

        foreach (Match match in absMatches)
        {
            if (match.Groups.Count > 1)
            {
                var url = match.Groups[1].Value.Trim().TrimEnd('"', '\'', ',', ';', ':');
                if (IsLikelyImageUrl(url))
                {
                    imageUrls.Add(ConvertToAbsoluteUrl(url, pageUrl));
                }
            }
        }
    }

    private List<string> ExtractUrlsFromCss(string css)
    {
        var urls = new List<string>();
        var pattern = @"url\(['""]?([^)'""])+['""]?\)";
        var matches = Regex.Matches(css, pattern);

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                urls.Add(match.Groups[1].Value.Trim());
            }
        }

        return urls;
    }

    private async Task<List<string>> ExtractLinksFromPageAsync(string pageUrl)
    {
        var links = new List<string>();

        try
        {
            var html = await FetchHtmlAsync(pageUrl);
            if (string.IsNullOrEmpty(html))
                return links;

            var document = await _browsingContext.OpenAsync(req => req.Content(html));
            var anchorElements = document.QuerySelectorAll("a");

            foreach (var anchor in anchorElements)
            {
                var href = anchor.GetAttribute("href");
                if (string.IsNullOrEmpty(href))
                    continue;

                var absoluteUrl = ConvertToAbsoluteUrl(href, pageUrl);
                
                if (Uri.TryCreate(absoluteUrl, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    // Normalize the URL (remove fragments, etc.)
                    var normalizedUrl = $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
                    if (!string.IsNullOrEmpty(uri.Query))
                        normalizedUrl += uri.Query;

                    links.Add(normalizedUrl);
                }
            }
        }
        catch (Exception ex)
        {
            if (_options.IsVerbose)
                Console.Error.WriteLine($"[WARN] Error extracting links from {pageUrl}: {ex.Message}");
        }

        return links;
    }

    private async Task<string?> FetchHtmlAsync(string url)
    {
        try
        {
            var response = await _httpClient.GetAsync(url);
            
            // Check for rate limiting
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // 429
            {
                _consecutiveErrors++;
                _currentBackoffMultiplier = Math.Min(_consecutiveErrors * 2, 8); // Max 8x backoff
                
                var backoffDelay = _options.RequestDelayMs * _currentBackoffMultiplier;
                Console.WriteLine($"[WARN] Rate limited (429). Backing off for {backoffDelay}ms...");
                await Task.Delay(backoffDelay);
                
                // Retry once after backoff
                response = await _httpClient.GetAsync(url);
            }
            
            response.EnsureSuccessStatusCode();
            
            // Reset error counter on success
            _consecutiveErrors = 0;
            _currentBackoffMultiplier = 1;
            
            return await response.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            // Check if it's a rate limiting issue
            if (ex.Message.Contains("429") || ex.Message.Contains("Too Many Requests"))
            {
                _consecutiveErrors++;
                _currentBackoffMultiplier = Math.Min(_consecutiveErrors * 2, 8);
                Console.WriteLine($"[WARN] Rate limiting detected. Consider using longer delays.");
            }
            
            throw new Exception($"Failed to fetch {url}", ex);
        }
    }

    private async Task DownloadImageAsync(string imageUrl)
    {
        // Skip if already downloaded
        if (DownloadedImages.Contains(imageUrl))
            return;

        DownloadedImages.Add(imageUrl);

        try
        {
            // Apply stealth mode delay with randomization before image download
            if (_options.StealthMode)
            {
                var delay = GetRandomizedDelay(_options.RequestDelayMs / 2);
                Log($"Delay: {delay}ms");
                await Task.Delay(delay);
            }

            var fileName = ExtractFileNameFromUrl(imageUrl);
            var originalFileName = fileName;
            
            // If conversion is requested, change the extension now
            if (!string.IsNullOrEmpty(_options.ConvertToFormat))
            {
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                var newExt = _options.ConvertToFormat == "jpg" ? ".jpg" : $".{_options.ConvertToFormat}";
                fileName = nameWithoutExt + newExt;
                Console.WriteLine($"[CONVERT] {originalFileName} will be converted to {fileName}");
            }
            
            var filePath = Path.Combine(_outputFolder ?? ".", fileName);
            filePath = GetUniqueFilePath(filePath);

            // Download image data
            Console.WriteLine($"[DOWNLOAD] Fetching {imageUrl}...");
            var imageData = await _httpClient.GetByteArrayAsync(imageUrl);
            Console.WriteLine($"[DOWNLOAD] Got {imageData.Length} bytes");
            
            // If conversion requested, convert before saving
            if (!string.IsNullOrEmpty(_options.ConvertToFormat))
            {
                Console.WriteLine($"[CONVERT] Starting conversion to {_options.ConvertToFormat.ToUpper()}...");
                var success = await ConvertAndSaveImageAsync(imageData, filePath, _options.ConvertToFormat);
                if (success)
                {
                    Console.WriteLine($"[?] {imageUrl} -> {Path.GetFileName(filePath)} ({_options.ConvertToFormat.ToUpper()})");
                }
                else
                {
                    Console.WriteLine($"[!] {imageUrl} -> {Path.GetFileName(filePath)} (conversion failed, saved as-is)");
                }
            }
            else
            {
                // Save directly without conversion
                await File.WriteAllBytesAsync(filePath, imageData);
                Console.WriteLine($"[+] {imageUrl} -> {Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Failed to download {imageUrl}: {ex.Message}");
            if (_options.IsVerbose)
            {
                Console.Error.WriteLine($"Stack: {ex.StackTrace}");
            }
        }
    }

    private bool IsSameDomain(string url, Uri originalUri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        return uri.Host.Equals(originalUri.Host, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsValidImageUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        try
        {
            var uri = new Uri(url, UriKind.RelativeOrAbsolute);
            if (uri.IsAbsoluteUri && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return false;

            var path = uri.IsAbsoluteUri ? uri.AbsolutePath : url;
            
            // Get the appropriate extension list based on options
            var extensions = _options.IncludeAnimatedGifs ? AllImageExtensions : ImageExtensions;
            
            return extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private bool IsLikelyImageUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        url = url.ToLower().Split('?')[0]; // Remove query parameters
        
        // Check for image extensions
        var extensions = _options.IncludeAnimatedGifs ? AllImageExtensions : ImageExtensions;
        return extensions.Any(ext => url.EndsWith(ext));
    }

    private string ConvertToAbsoluteUrl(string relativeUrl, string pageUrl)
    {
        if (string.IsNullOrEmpty(relativeUrl))
            return "";

        try
        {
            if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out _))
                return relativeUrl;

            var pageUri = new Uri(pageUrl);
            var absoluteUri = new Uri(pageUri, relativeUrl);
            return absoluteUri.ToString();
        }
        catch
        {
            return "";
        }
    }

    private string ExtractFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var fileName = Path.GetFileName(uri.LocalPath);

            if (string.IsNullOrEmpty(fileName) || fileName == "/")
                fileName = "image";

            // Ensure it has an extension
            if (!Path.HasExtension(fileName))
                fileName += ".jpg";

            // Sanitize the filename
            var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            var invalidRegex = new Regex($"[{invalidChars}]");
            fileName = invalidRegex.Replace(fileName, "_");

            return fileName;
        }
        catch
        {
            return "image.jpg";
        }
    }

    private string GetUniqueFilePath(string filePath)
    {
        if (!File.Exists(filePath))
            return filePath;

        var directory = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);

        int counter = 1;
        while (true)
        {
            var newFileName = $"{fileName}_{counter}{extension}";
            var newFilePath = Path.Combine(directory ?? ".", newFileName);
            
            if (!File.Exists(newFilePath))
                return newFilePath;

            counter++;
        }
    }

    private string DetermineOutputFolder(string url)
    {
        // If custom output folder is specified, use it
        if (!string.IsNullOrEmpty(_options.OutputFolder))
            return _options.OutputFolder;

        // Try to use page title if available
        try
        {
            var html = FetchHtmlAsync(url).Result;
            if (!string.IsNullOrEmpty(html))
            {
                var document = _browsingContext.OpenAsync(req => req.Content(html)).Result;
                var title = document.Title;

                if (!string.IsNullOrEmpty(title) && title.Length > 2)
                {
                    var sanitized = Regex.Replace(title, @"[^\w\s-]", "");
                    sanitized = Regex.Replace(sanitized, @"\s+", "_");
                    
                    if (sanitized.Length > 3)
                        return sanitized;
                }
            }
        }
        catch
        {
            // Fallback if title retrieval fails
        }

        // Fallback to domain and path based name
        var uri = new Uri(url);
        var domain = uri.Host.Replace("www.", "");
        var pathSlug = uri.AbsolutePath.Trim('/').Replace("/", "_");
        
        if (!string.IsNullOrEmpty(pathSlug))
            return $"{domain}_{pathSlug}";
        else
            return $"{domain}_images";
    }

    private List<string> ParseSrcSet(string srcSet)
    {
        var urls = new List<string>();
        
        // Parse srcset attribute: "url1 1x, url2 2x, url3 3x"
        // Return all URLs, not just the largest
        var entries = srcSet.Split(',');

        foreach (var entry in entries)
        {
            var parts = entry.Trim().Split(' ');
            if (parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]))
            {
                urls.Add(parts[0]);
            }
        }

        return urls;
    }

    private void Log(string message)
    {
        if (_options.IsVerbose)
            Console.WriteLine($"* {message}");
    }

    private async Task<bool> ConvertAndSaveImageAsync(byte[] imageData, string targetFile, string targetFormat)
    {
        try
        {
            Console.WriteLine($"[CONVERT] Loading image from {imageData.Length} bytes...");
            
            using var inputStream = new MemoryStream(imageData);
            using var image = await Image.LoadAsync(inputStream);
            
            Console.WriteLine($"[CONVERT] Image loaded: {image.Width}x{image.Height}");
            Console.WriteLine($"[CONVERT] Converting to {targetFormat.ToUpper()}...");

            switch (targetFormat.ToLower())
            {
                case "jpg":
                case "jpeg":
                    var jpegEncoder = new JpegEncoder
                    {
                        Quality = _options.JpegQuality
                    };
                    await image.SaveAsync(targetFile, jpegEncoder);
                    Console.WriteLine($"[CONVERT] Saved as JPEG (quality: {_options.JpegQuality})");
                    break;

                case "png":
                    var pngEncoder = new PngEncoder();
                    await image.SaveAsync(targetFile, pngEncoder);
                    Console.WriteLine($"[CONVERT] Saved as PNG");
                    break;

                case "gif":
                    var gifEncoder = new GifEncoder();
                    await image.SaveAsync(targetFile, gifEncoder);
                    Console.WriteLine($"[CONVERT] Saved as GIF");
                    break;

                default:
                    Console.WriteLine($"[ERROR] Unknown format '{targetFormat}'");
                    await File.WriteAllBytesAsync(targetFile, imageData);
                    return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERROR] Conversion failed: {ex.Message}");
            if (_options.IsVerbose)
            {
                Console.Error.WriteLine($"[ERROR] Details: {ex}");
            }
            Console.WriteLine($"[CONVERT] Saving original instead...");
            
            try
            {
                await File.WriteAllBytesAsync(targetFile, imageData);
                return false;
            }
            catch (Exception saveEx)
            {
                Console.Error.WriteLine($"[ERROR] Failed to save original: {saveEx.Message}");
                return false;
            }
        }
    }
}
