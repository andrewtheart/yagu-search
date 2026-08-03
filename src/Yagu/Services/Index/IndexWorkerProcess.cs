using System.Diagnostics;

namespace Yagu.Services.Index;

internal interface IIndexWorkerProcess : IDisposable
{
    event EventHandler? Exited;

    bool HasExited { get; }

    int Id { get; }

    nint Handle { get; }

    long PeakWorkingSetBytes { get; }

    TextWriter StandardInput { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    bool Start();

    void Refresh();

    void Kill();
}

internal static class IndexWorkerProcessFactory
{
    internal static IIndexWorkerProcess Create(ProcessStartInfo startInfo)
        => new SystemIndexWorkerProcess(startInfo);

    private sealed class SystemIndexWorkerProcess : IIndexWorkerProcess
    {
        private readonly Process _process;

        internal SystemIndexWorkerProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        }

        public event EventHandler? Exited
        {
            add => _process.Exited += value;
            remove => _process.Exited -= value;
        }

        public bool HasExited => _process.HasExited;

        public int Id => _process.Id;

        public nint Handle => _process.SafeHandle.DangerousGetHandle();

        public long PeakWorkingSetBytes => _process.PeakWorkingSet64;

        public TextWriter StandardInput => _process.StandardInput;

        public StreamReader StandardOutput => _process.StandardOutput;

        public StreamReader StandardError => _process.StandardError;

        public bool Start() => _process.Start();

        public void Refresh() => _process.Refresh();

        public void Kill() => _process.Kill(entireProcessTree: true);

        public void Dispose() => _process.Dispose();
    }
}