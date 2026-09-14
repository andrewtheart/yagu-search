namespace Yagu.Services.Cli.MachineOutput;

/// <summary>
/// The shape of the CLI machine protocol, shared by the writer (in the app), the reader (in the MCP
/// bridge) and the tests, so no two of them can drift apart.
/// </summary>
internal static class MachineProtocol
{
    /// <summary>Bumped only for a breaking envelope change; consumers reject a version they don't know.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Record type tokens carried in the envelope's <c>type</c> field.</summary>
    public static class Types
    {
        /// <summary>A search hit.</summary>
        public const string Match = "match";

        /// <summary>A non-search payload record: a read line, a calc answer, an index status.</summary>
        public const string Result = "result";

        /// <summary>How a natural-language query was interpreted.</summary>
        public const string Plan = "plan";

        /// <summary>A throttled scan heartbeat.</summary>
        public const string Progress = "progress";

        /// <summary>A non-fatal condition.</summary>
        public const string Warning = "warning";

        /// <summary>An environment or configuration fact.</summary>
        public const string Diagnostic = "diagnostic";

        /// <summary>The terminal failure.</summary>
        public const string Error = "error";

        /// <summary>Always the last record of a run.</summary>
        public const string Summary = "summary";
    }
}
