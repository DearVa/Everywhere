using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Media.Imaging;
using MaaFramework.Binding;
using MaaFramework.Binding.Buffers;
using MaaFramework.Binding.Custom;

namespace Everywhere.Interop;

/// <summary>
/// MaaFramework-based OCR service implementation.
/// </summary>
public sealed class MaaOcrService : IOcrService, IDisposable
{
    private const string OcrDownloadUrl = "https://download.maafw.xyz/MaaCommonAssets/OCR/ppocr_v5/ppocr_v5-zh_cn.zip";
    private const string OcrZipSha256 = "c98af2a094b75986dd73b1f362a30e9eaeeffa1cf28a8a9fea80e6ccc8538baf";
    private static readonly string[] OcrRequiredFiles = ["det.onnx", "keys.txt", "rec.onnx"];

    private readonly string _bundleDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Everywhere", "MaaBundle");
    private readonly string _ocrDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Everywhere", "MaaBundle", "model", "ocr");
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private MaaResource? _resource;
    private bool _isInitialized;
    private bool _disposed;

    public async ValueTask<bool> EnsureModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return true;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isInitialized) return true;

            if (!CheckOcrFilesExist())
            {
                var success = await DownloadAndExtractOcrAsync(progress, cancellationToken);
                if (!success) return false;
            }
            else
            {
                progress?.Report(1.0);
            }

            EnsurePipelineDir();
            InitializeResource();
            _isInitialized = true;
            return true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<OcrResult?> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream();
        image.Save(stream);
        return await RecognizeAsync(stream.ToArray(), cancellationToken);
    }

    public async Task<OcrResult?> RecognizeAsync(byte[] imageData, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_isInitialized)
        {
            var ready = await EnsureModelAsync(cancellationToken: cancellationToken);
            if (!ready) return null;
        }

        return await Task.Run(() => PerformOcrInternal(imageData), cancellationToken);
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private bool CheckOcrFilesExist()
    {
        if (!Directory.Exists(_ocrDir)) return false;
        return OcrRequiredFiles.All(f => File.Exists(Path.Combine(_ocrDir, f)));
    }

    private void EnsurePipelineDir()
    {
        var pipelineDir = Path.Combine(_bundleDir, "pipeline");
        if (Directory.Exists(pipelineDir)) return;

        Directory.CreateDirectory(pipelineDir);
        File.WriteAllText(Path.Combine(pipelineDir, "sample.json"), "{}");
    }

    private void InitializeResource()
    {
        _resource = new MaaResource();
        var loadJob = _resource.AppendBundle(_bundleDir);
        var status = loadJob.Wait();

        if (status != MaaJobStatus.Succeeded)
            throw new InvalidOperationException($"Failed to load MaaResource: {status}");
    }

    private async Task<bool> DownloadAndExtractOcrAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_ocrDir);
        var modelDir = Path.GetDirectoryName(_ocrDir);
        ArgumentNullException.ThrowIfNull(modelDir);
        Directory.CreateDirectory(modelDir);
        var zipFile = Path.Combine(modelDir, "ppocr_v5-zh_cn.zip");

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var response = await httpClient.GetAsync(OcrDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(zipFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                if (totalBytes > 0)
                    fileStream.SetLength(totalBytes);

                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                        progress?.Report((double)downloadedBytes / totalBytes * 0.9);
                }
            }
            var computedSha256 = ComputeSha256(zipFile);
            if (computedSha256 != OcrZipSha256)
            {
                File.Delete(zipFile);
                return false;
            }

            ZipFile.ExtractToDirectory(zipFile, _ocrDir, true);

            foreach (var requiredFile in OcrRequiredFiles)
            {
                var targetPath = Path.Combine(_ocrDir, requiredFile);
                if (File.Exists(targetPath)) continue;

                var foundFiles = Directory.GetFiles(_ocrDir, requiredFile, SearchOption.TopDirectoryOnly);
                if (foundFiles.Length > 0)
                    File.Move(foundFiles[0], targetPath);
            }

            foreach (var dir in Directory.GetDirectories(_ocrDir))
                Directory.Delete(dir, true);

            File.Delete(zipFile);
            progress?.Report(1.0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private OcrResult? PerformOcrInternal(byte[] imageData)
    {
        if (_resource == null) return null;

        using var imageBuffer = new MaaImageBuffer();
        if (!imageBuffer.TrySetEncodedData(imageData)) return null;

        using var customController = new MaaCustomController(
            new StaticImageController(imageBuffer),
            LinkOption.Start,
            CheckStatusOption.None);

        if (!customController.LinkStart().Wait().IsSucceeded())
            return null;

        using var tasker = new MaaTasker
        {
            Controller = customController,
            Resource = _resource,
            DisposeOptions = DisposeOptions.None
        };

        if (!tasker.IsInitialized) return null;

        const string ocrPipeline = """
        {
            "OCR_Entry": {
                "recognition": "OCR",
                "action": "DoNothing"
            }
        }
        """;

        var job = tasker.AppendTask("OCR_Entry", ocrPipeline);
        var taskStatus = job.Wait();
        if (taskStatus != MaaJobStatus.Succeeded) return null;

        var taskDetail = job.QueryTaskDetail();
        if (taskDetail == null || taskDetail.NodeIdList.Count == 0) return null;

        if (!tasker.GetNodeDetail(taskDetail.NodeIdList[0], out _, out var recognitionId, out _, out _))
            return null;

        using var hitBox = new MaaRectBuffer();
        if (!tasker.GetRecognitionDetail<MaaImageBuffer>(recognitionId, out _, out _, out _, hitBox, out var detailJson, null, null))
            return null;

        return ParseOcrResult(detailJson);
    }

    private static OcrResult? ParseOcrResult(string? detailJson)
    {
        if (string.IsNullOrEmpty(detailJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(detailJson);
            var root = doc.RootElement;

            var all = ParseTextResults(root, "all");
            var filtered = ParseTextResults(root, "filtered");
            var best = root.TryGetProperty("best", out var bestProp) && bestProp.ValueKind != JsonValueKind.Null
                ? ParseSingleResult(bestProp)
                : null;

            return new OcrResult(all, best, filtered);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<OcrTextResult> ParseTextResults(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<OcrTextResult>();
        foreach (var item in prop.EnumerateArray())
        {
            var result = ParseSingleResult(item);
            if (result != null)
                results.Add(result);
        }

        return results;
    }

    private static OcrTextResult? ParseSingleResult(JsonElement item)
    {
        if (!item.TryGetProperty("text", out var textProp)) return null;
        var text = textProp.GetString() ?? "";

        var score = item.TryGetProperty("score", out var scoreProp) ? scoreProp.GetDouble() : 0;

        var box = new[] { 0, 0, 0, 0 };
        if (item.TryGetProperty("box", out var boxProp) && boxProp.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var b in boxProp.EnumerateArray())
            {
                if (i < 4) box[i++] = b.GetInt32();
            }
        }

        return new OcrTextResult(text, score, box);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resource?.Dispose();
        _initLock.Dispose();
    }
}

/// <summary>
/// Custom controller that provides a static image for OCR.
/// </summary>
internal sealed class StaticImageController : IMaaCustomController
{
    private readonly IMaaImageBuffer _image;
    private readonly int _width;
    private readonly int _height;

    public string Name { get; set; } = nameof(StaticImageController);

    public StaticImageController(IMaaImageBuffer image)
    {
        _image = image;
        _width = image.Width;
        _height = image.Height;
    }

    public bool Connect() => true;

    public bool RequestUuid(in IMaaStringBuffer buffer) => buffer.TrySetValue("StaticImageController");

    public ControllerFeatures GetFeatures() => ControllerFeatures.None;

    public bool RequestResolution(out int width, out int height)
    {
        width = _width;
        height = _height;
        return true;
    }

    public bool StartApp(string intent) => false;
    public bool StopApp(string intent) => false;

    public bool Screencap(in IMaaImageBuffer buffer) => _image.TryCopyTo(buffer);

    public bool Click(int x, int y) => true;
    public bool Swipe(int x1, int y1, int x2, int y2, int duration) => true;
    public bool TouchDown(int contact, int x, int y, int pressure) => true;
    public bool TouchMove(int contact, int x, int y, int pressure) => true;
    public bool TouchUp(int contact) => true;
    public bool ClickKey(int keycode) => true;
    public bool InputText(string text) => true;
    public bool KeyDown(int keycode) => true;
    public bool KeyUp(int keycode) => true;
    public bool Scroll(int dx, int dy) => true;

    public void Dispose()
    {
        // Image buffer is owned by caller
    }
}
