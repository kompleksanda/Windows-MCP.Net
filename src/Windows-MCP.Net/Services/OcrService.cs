using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Sdcb.OpenVINO.PaddleOCR;
using Sdcb.OpenVINO.PaddleOCR.Models.Online;
using Sdcb.OpenVINO.PaddleOCR.Models;
using System.Runtime.InteropServices;
using Interface;


namespace WindowsMCP.Net.Services;

/// <summary>
/// Implementation of OCR services using PaddleOCR.
/// </summary>
public class OcrService : IOcrService, IDisposable
{
    private readonly ILogger<OcrService> _logger;
    private static readonly Lazy<OcrService> _instance = new Lazy<OcrService>(() => new OcrService());
    private static readonly object _lock = new object();
    private static readonly ConcurrentDictionary<string, FullOcrModel> _models = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed = false;

    // Windows API imports for screen capture and active application inspection
    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hGDIObj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hDestDC, int x, int y, int nWidth, int nHeight, IntPtr hSrcDC, int xSrc, int ySrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // System metrics constants
    private const int SM_CXSCREEN = 0; // Width of the primary screen
    private const int SM_CYSCREEN = 1; // Height of the primary screen
    private const int SM_XVIRTUALSCREEN = 76; // Left of the virtual screen across all monitors
    private const int SM_YVIRTUALSCREEN = 77; // Top of the virtual screen across all monitors
    private const int SM_CXVIRTUALSCREEN = 78; // Width of the virtual screen across all monitors
    private const int SM_CYVIRTUALSCREEN = 79; // Height of the virtual screen across all monitors

    private static (int Left, int Top, int Width, int Height) GetVirtualScreenBounds()
    {
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width > 0 && height > 0)
        {
            return (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), width, height);
        }
        return (0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
    }

    /// <summary>
    /// 获取单例实例
    /// </summary>
    public static OcrService Instance => _instance.Value;

    /// <summary>
    /// 初始化OCR服务
    /// </summary>
    /// <param name="logger">日志记录器</param>
    public OcrService(ILogger<OcrService> logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<OcrService>.Instance;
    }

    /// <summary>
    /// 私有构造函数，用于单例模式
    /// </summary>
    private OcrService()
    {
        _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<OcrService>.Instance;
    }

    /// <summary>
    /// Resolve the best OnlineFullModels profile based on:
    /// 1. Explicit WINDOWS_MCP_OCR_LANGUAGE environment variable override
    /// 2. Target search text script (when searching for non-ASCII characters)
    /// 3. Active foreground application window title script & thread keyboard layout/locale
    /// 4. Windows OS UI culture (CultureInfo.CurrentUICulture / InstalledUICulture)
    /// </summary>
    private (string Key, OnlineFullModels Model) ResolveModelProfile(string? hintText = null)
    {
        var envLang = Environment.GetEnvironmentVariable("WINDOWS_MCP_OCR_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(envLang))
        {
            return MapCultureToModel(envLang.Trim());
        }

        if (!string.IsNullOrWhiteSpace(hintText))
        {
            var scriptModel = DetectScriptModel(hintText);
            if (scriptModel != null)
            {
                return scriptModel.Value;
            }
        }

        try
        {
            IntPtr fgWindow = GetForegroundWindow();
            if (fgWindow != IntPtr.Zero)
            {
                var sb = new StringBuilder(512);
                if (GetWindowText(fgWindow, sb, sb.Capacity) > 0)
                {
                    var titleModel = DetectScriptModel(sb.ToString());
                    if (titleModel != null)
                    {
                        return titleModel.Value;
                    }
                }

                uint threadId = GetWindowThreadProcessId(fgWindow, out _);
                if (threadId != 0)
                {
                    IntPtr hkl = GetKeyboardLayout(threadId);
                    int langId = (int)((ulong)hkl & 0xFFFF);
                    if (langId > 0)
                    {
                        var appCulture = CultureInfo.GetCultureInfo(langId);
                        if (!appCulture.TwoLetterISOLanguageName.Equals("iv", StringComparison.OrdinalIgnoreCase))
                        {
                            return MapCultureToModel(appCulture.Name);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect foreground application locale; falling back to Windows UI culture");
        }

        var osCulture = CultureInfo.CurrentUICulture.Name;
        if (string.IsNullOrWhiteSpace(osCulture))
        {
            osCulture = CultureInfo.InstalledUICulture.Name;
        }
        return MapCultureToModel(osCulture);
    }

    private static (string Key, OnlineFullModels Model)? DetectScriptModel(string text)
    {
        foreach (char c in text)
        {
            if ((c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF'))
                return ("JapanV4", OnlineFullModels.JapanV4);
            if ((c >= '\uAC00' && c <= '\uD7AF') || (c >= '\u1100' && c <= '\u11FF'))
                return ("KoreanV4", OnlineFullModels.KoreanV4);
            if (c >= '\u4E00' && c <= '\u9FFF')
                return ("ChineseV4", OnlineFullModels.ChineseV4);
            if (c >= '\u0400' && c <= '\u04FF')
                return ("CyrillicV3", OnlineFullModels.CyrillicV3);
            if (c >= '\u0600' && c <= '\u06FF')
                return ("ArabicV4", OnlineFullModels.ArabicV4);
            if (c >= '\u0900' && c <= '\u097F')
                return ("DevanagariV4", OnlineFullModels.DevanagariV4);
        }
        return null;
    }

    private static (string Key, OnlineFullModels Model) MapCultureToModel(string cultureName)
    {
        var normalized = cultureName.Trim().ToLowerInvariant();
        var lang = normalized.Split('-', '_')[0];

        return lang switch
        {
            "en" => ("EnglishV4", OnlineFullModels.EnglishV4),
            "zh" when normalized.Contains("tw") || normalized.Contains("hk") || normalized.Contains("mo") || normalized.Contains("hant")
                => ("TraditionalChineseV3", OnlineFullModels.TraditionalChineseV3),
            "zh" => ("ChineseV4", OnlineFullModels.ChineseV4),
            "ja" => ("JapanV4", OnlineFullModels.JapanV4),
            "ko" => ("KoreanV4", OnlineFullModels.KoreanV4),
            "ar" or "fa" or "ur" => ("ArabicV4", OnlineFullModels.ArabicV4),
            "hi" or "mr" or "ne" or "sa" => ("DevanagariV4", OnlineFullModels.DevanagariV4),
            "ta" => ("TamilV4", OnlineFullModels.TamilV4),
            "te" => ("TeluguV4", OnlineFullModels.TeluguV4),
            "kn" => ("KannadaV4", OnlineFullModels.KannadaV4),
            "ru" or "uk" or "bg" or "sr" or "be" or "kk" or "mn" => ("CyrillicV3", OnlineFullModels.CyrillicV3),
            "fr" or "de" or "es" or "it" or "pt" or "nl" or "pl" or "sv" or "tr" or "vi" or "cs" or "ro" or "hu"
                => ("LatinV3", OnlineFullModels.LatinV3),
            _ => ("EnglishV4", OnlineFullModels.EnglishV4)
        };
    }

    /// <summary>
    /// Initialize and return the appropriate OCR model based on Windows environment and active application.
    /// </summary>
    private Task<FullOcrModel> GetOrInitializeModelAsync(string? hintText = null)
    {
        var (key, onlineModel) = ResolveModelProfile(hintText);
        if (_models.TryGetValue(key, out var cachedModel))
        {
            return Task.FromResult(cachedModel);
        }

        lock (_lock)
        {
            if (_models.TryGetValue(key, out cachedModel))
            {
                return Task.FromResult(cachedModel);
            }

            try
            {
                _logger.LogInformation("Initializing OCR model '{ModelKey}'...", key);

                try
                {
                    var version = Cv2.GetVersionString();
                    _logger.LogInformation("OpenCV version: {Version}", version);
                }
                catch (Exception cvEx)
                {
                    _logger.LogError(cvEx, "Failed to get OpenCV version - native libraries may not be loaded correctly");
                    throw new InvalidOperationException("OpenCV native libraries are not available. This may be due to missing runtime dependencies in the dnx environment.", cvEx);
                }

                var runtimeInfo = RuntimeInformation.RuntimeIdentifier;
                var architecture = RuntimeInformation.ProcessArchitecture;
                _logger.LogInformation("Runtime: {Runtime}, Architecture: {Architecture}", runtimeInfo, architecture);

                _logger.LogInformation("Downloading OCR model '{ModelKey}'...", key);
                var originalOut = Console.Out;
                FullOcrModel downloaded;
                try
                {
                    Console.SetOut(Console.Error);
                    downloaded = onlineModel.DownloadAsync().Result;
                }
                finally
                {
                    Console.SetOut(originalOut);
                }

                _models[key] = downloaded;
                _logger.LogInformation("OCR model '{ModelKey}' downloaded and initialized successfully", key);
                return Task.FromResult(downloaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize OCR model '{ModelKey}'. Error: {Message}", key, ex.Message);
                throw;
            }
        }
    }

    /// <summary>
    /// Extract text from a specific region of the screen.
    /// </summary>
    public async Task<(string Text, int Status)> ExtractTextFromRegionAsync(int x, int y, int width, int height, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Extracting text from screen region: ({X}, {Y}, {Width}, {Height})", x, y, width, height);
            
            using var bitmap = CaptureScreenRegion(x, y, width, height);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            
            var (text, status) = await ExtractTextFromImageAsync(stream, cancellationToken);
            return (text, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from screen region");
            return (string.Empty, 1);
        }
    }

    /// <summary>
    /// Extract text from the entire screen.
    /// </summary>
    public async Task<(string Text, int Status)> ExtractTextFromScreenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Extracting text from entire virtual screen");
            
            var (left, top, screenWidth, screenHeight) = GetVirtualScreenBounds();
            return await ExtractTextFromRegionAsync(left, top, screenWidth, screenHeight, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from screen");
            return (string.Empty, 1);
        }
    }

    /// <summary>
    /// Find specific text on the screen.
    /// </summary>
    public async Task<(bool Found, int Status)> FindTextOnScreenAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Searching for text on screen: {Text}", text);
            
            var model = await GetOrInitializeModelAsync(text);
            var (left, top, screenWidth, screenHeight) = GetVirtualScreenBounds();
            using var bitmap = CaptureScreenRegion(left, top, screenWidth, screenHeight);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;

            using (PaddleOcrAll all = new(model)
            {
                AllowRotateDetection = true,
                Enable180Classification = true,
            })
            {
                Mat src = Cv2.ImDecode(StreamToByte(stream), ImreadModes.Color);
                PaddleOcrResult result = all.Run(src);
                bool found = result.Text.Contains(text, StringComparison.OrdinalIgnoreCase);
                return (found, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for text on screen");
            return (false, 1);
        }
    }

    /// <summary>
    /// Get coordinates of specific text on the screen.
    /// </summary>
    public async Task<(System.Drawing.Point? Coordinates, int Status)> GetTextCoordinatesAsync(string text, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting coordinates for text: {Text}", text);
            
            var model = await GetOrInitializeModelAsync(text);
            
            var (left, top, screenWidth, screenHeight) = GetVirtualScreenBounds();
            using var bitmap = CaptureScreenRegion(left, top, screenWidth, screenHeight);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            
            using (PaddleOcrAll all = new(model)
            {
                AllowRotateDetection = true,
                Enable180Classification = true,
            })
            {
                Mat src = Cv2.ImDecode(StreamToByte(stream), ImreadModes.Color);
                PaddleOcrResult result = all.Run(src);
                
                // 查找包含指定文本的区域
                foreach (var region in result.Regions)
                {
                    if (region.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        // 返回文本区域的中心点坐标 (offset by virtual screen origin for multi-monitor setups)
                        var centerX = left + (int)region.Rect.Center.X;
                        var centerY = top + (int)region.Rect.Center.Y;
                        return (new System.Drawing.Point(centerX, centerY), 0);
                    }
                }
            }
            
            return (null, 0); // 未找到文本
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting text coordinates");
            return (null, 1);
        }
    }

    /// <summary>
    /// Extract text from an image stream.
    /// </summary>
    public async Task<(string Text, int Status)> ExtractTextFromImageAsync(Stream imageStream, CancellationToken cancellationToken = default)
    {
        try
        {
            var model = await GetOrInitializeModelAsync();

            using (PaddleOcrAll all = new(model)
            {
                AllowRotateDetection = true,
                Enable180Classification = true,
            })
            {
                Mat src = Cv2.ImDecode(StreamToByte(imageStream), ImreadModes.Color);
                PaddleOcrResult result = all.Run(src);
                return (result.Text, 0);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting text from image: {Message}", ex.Message);
            return (string.Empty, 1);
        }
    }

    /// <summary>
    /// 截取屏幕指定区域
    /// </summary>
    private Bitmap CaptureScreenRegion(int x, int y, int width, int height)
    {
        IntPtr desktopWindow = GetDesktopWindow();
        IntPtr desktopDC = GetWindowDC(desktopWindow);
        IntPtr memoryDC = CreateCompatibleDC(desktopDC);
        IntPtr bitmap = CreateCompatibleBitmap(desktopDC, width, height);
        IntPtr oldBitmap = SelectObject(memoryDC, bitmap);

        BitBlt(memoryDC, 0, 0, width, height, desktopDC, x, y, 0x00CC0020); // SRCCOPY

        SelectObject(memoryDC, oldBitmap);
        DeleteDC(memoryDC);
        ReleaseDC(desktopWindow, desktopDC);

        Bitmap result = Image.FromHbitmap(bitmap);
        DeleteObject(bitmap);

        return result;
    }

    /// <summary>
    /// 将Stream转换为byte数组
    /// </summary>
    private byte[] StreamToByte(Stream stream)
    {
        using (var memoryStream = new MemoryStream())
        {
            byte[] buffer = new byte[1024];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memoryStream.Write(buffer, 0, bytesRead);
            }
            return memoryStream.ToArray();
        }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源的具体实现
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    _models.Clear();
                }
            }
            _disposed = true;
        }
    }
}