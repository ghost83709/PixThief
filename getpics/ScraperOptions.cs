namespace PixThief;

class ScraperOptions
{
    public string? Url { get; set; }
    public bool IsDomainMode { get; set; }
    public string? OutputFolder { get; set; }
    public int MaxPages { get; set; } = 100;
    public bool IsVerbose { get; set; }
    public bool IncludeAnimatedGifs { get; set; }
    public bool StealthMode { get; set; }
    public bool RandomizeDelays { get; set; } // Only true for --stealth, false for --delay
    
    // Research-backed safe default: 1000ms (1 second)
    // - Avoids rate limiting (most sites allow 1-2 req/sec)
    // - Prevents IP bans
    // - Bypasses basic bot detection
    // - With ±40% randomization: 600-1400ms range
    public int RequestDelayMs { get; set; } = 1000;
    
    // Image conversion options
    public string? ConvertToFormat { get; set; } // "jpg", "png", or "gif"
    public int JpegQuality { get; set; } = 90; // Quality for JPEG conversion (1-100)
}
