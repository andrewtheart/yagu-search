using System.Text;
using System.Text.Json;

namespace Yagu.OcrWorker;

/// <summary>
/// Entry point for the out-of-process OCR worker. Communicates with Yagu over stdin/stdout using
/// line-delimited JSON (see <see cref="OcrRequest"/> / <see cref="OcrEnvelope"/>). Diagnostic logs go
/// to stderr so they never corrupt the protocol stream.
/// <para>
/// The OCR backend is chosen at startup via the <c>YAGU_OCR_ENGINE</c> environment variable
/// (<c>paddle</c> — the default — or <c>tesseract</c>). Both backends speak the identical protocol.
/// </para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        // The protocol stream must be UTF-8 and must contain ONLY protocol JSON lines.
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        TextWriter stdout = Console.Out;

        // PaddleSharp's model downloader writes progress ("Downloading ...", "Extracting ...") via
        // Console.WriteLine. Those non-JSON lines would corrupt the protocol stream on the first run
        // (before models are cached) and hang the host's JSON reader. Redirect Console.Out to stderr so
        // any library writes go to the diagnostic channel; the protocol keeps using the captured stdout.
        Console.SetOut(Console.Error);

        IWorkerOcrEngine engine;
        try
        {
            engine = await CreateEngineAsync().ConfigureAwait(false);
            WriteEnvelope(stdout, new OcrEnvelope { Type = "ready" });
            Log("ready");
        }
        catch (Exception ex)
        {
            WriteEnvelope(stdout, new OcrEnvelope { Type = "error", Stage = "init", Message = ex.Message });
            Log("INIT FAILED: " + ex);
            return 1;
        }

        try
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                // Defensive: a host whose stdin writer emits a UTF-8 BOM would prepend U+FEFF to the
                // first line. Strip it (and surrounding whitespace) so the request still parses.
                line = line.Trim('\uFEFF', '\u200B').Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                OcrRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize(line, OcrJsonContext.Default.OcrRequest);
                }
                catch (Exception ex)
                {
                    Log("bad request json: " + ex.Message);
                    continue;
                }

                if (request is null)
                {
                    continue;
                }

                WriteEnvelope(stdout, Recognize(engine, request));
            }
        }
        finally
        {
            engine.Dispose();
            Log("shutdown");
        }

        return 0;
    }

    private static async Task<IWorkerOcrEngine> CreateEngineAsync()
    {
        string engineId = (Environment.GetEnvironmentVariable("YAGU_OCR_ENGINE") ?? "paddle").Trim().ToLowerInvariant();
        Log($"engine={engineId}");

        return engineId switch
        {
            "tesseract" => await TesseractWorkerEngine.CreateAsync(Log).ConfigureAwait(false),
            _ => await PaddleWorkerEngine.CreateAsync(Log).ConfigureAwait(false),
        };
    }

    private static OcrEnvelope Recognize(IWorkerOcrEngine engine, OcrRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
            {
                return new OcrEnvelope { Type = "result", Id = request.Id, Ok = false, Error = "file not found" };
            }

            string text = engine.Recognize(request.Path);
            return new OcrEnvelope { Type = "result", Id = request.Id, Ok = true, Text = text };
        }
        catch (Exception ex)
        {
            return new OcrEnvelope { Type = "result", Id = request.Id, Ok = false, Error = ex.Message };
        }
    }

    private static void WriteEnvelope(TextWriter stdout, OcrEnvelope envelope)
    {
        string json = JsonSerializer.Serialize(envelope, OcrJsonContext.Default.OcrEnvelope);
        stdout.Write(json);
        stdout.Write('\n');
        stdout.Flush();
    }

    private static void Log(string message) => Console.Error.WriteLine("[ocrworker] " + message);
}
