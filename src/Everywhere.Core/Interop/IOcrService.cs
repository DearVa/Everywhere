using Avalonia.Media.Imaging;

namespace Everywhere.Interop;

/// <summary>
/// Represents an OCR text recognition result.
/// </summary>
/// <param name="Text">The recognized text.</param>
/// <param name="Score">The confidence score (0.0 - 1.0).</param>
/// <param name="Box">The bounding box [x, y, width, height].</param>
public record OcrTextResult(string Text, double Score, int[] Box);

/// <summary>
/// Represents the full OCR recognition result.
/// </summary>
/// <param name="All">All detected text regions.</param>
/// <param name="Best">The best match based on score.</param>
/// <param name="Filtered">Filtered results above threshold.</param>
public record OcrResult(
    IReadOnlyList<OcrTextResult> All,
    OcrTextResult? Best,
    IReadOnlyList<OcrTextResult> Filtered);

/// <summary>
/// OCR service interface for text recognition from images.
/// </summary>
public interface IOcrService
{
    /// <summary>
    /// Ensures the OCR model is downloaded and ready.
    /// </summary>
    /// <param name="progress">Optional progress callback (0.0 - 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if model is ready, false if download failed.</returns>
    ValueTask<bool> EnsureModelAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs OCR on the given image.
    /// </summary>
    /// <param name="image">The image to recognize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OCR result or null if recognition failed.</returns>
    Task<OcrResult?> RecognizeAsync(Bitmap image, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs OCR on image data.
    /// </summary>
    /// <param name="imageData">PNG or JPEG encoded image data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>OCR result or null if recognition failed.</returns>
    Task<OcrResult?> RecognizeAsync(byte[] imageData, CancellationToken cancellationToken = default);
}
