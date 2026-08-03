using System.Diagnostics;

namespace Yagu.Services.Ocr;

internal interface IOcrWorkerProcess : IDisposable
{
    event EventHandler? Exited;

    bool HasExited { get; }

    TextWriter StandardInput { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    bool Start();

    Task WaitForExitAsync(CancellationToken cancellationToken);

    void Kill();
}

internal static class OcrWorkerProcessFactory
{
    internal static IOcrWorkerProcess Create(ProcessStartInfo startInfo)
        => new SystemOcrWorkerProcess(startInfo);

    private sealed class SystemOcrWorkerProcess : IOcrWorkerProcess
    {
        private readonly Process _process;

        internal SystemOcrWorkerProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        }

        public event EventHandler? Exited
        {
            add => _process.Exited += value;
            remove => _process.Exited -= value;
        }

        public bool HasExited => _process.HasExited;

        public TextWriter StandardInput => _process.StandardInput;

        public StreamReader StandardOutput => _process.StandardOutput;

        public StreamReader StandardError => _process.StandardError;

        public bool Start() => _process.Start();

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _process.WaitForExitAsync(cancellationToken);

        public void Kill() => _process.Kill(entireProcessTree: true);

        public void Dispose() => _process.Dispose();
    }
}