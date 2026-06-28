namespace Yagu.OcrWorker;

/// <summary>
/// A single OCR backend hosted by the worker process. Implementations own any native resources for
/// the lifetime of the worker and recognize one image at a time (the worker is single-threaded over
/// its stdio protocol). Implementations should throw on hard failures; <see cref="Program"/> wraps
/// each call and reports the message back to Yagu as a failed result.
/// </summary>
internal interface IWorkerOcrEngine : IDisposable
{
    /// <summary>Recognizes text in the image at <paramref name="imagePath"/>. Returns the recognized
    /// text (possibly empty). Throws on unrecoverable errors.</summary>
    string Recognize(string imagePath);
}
