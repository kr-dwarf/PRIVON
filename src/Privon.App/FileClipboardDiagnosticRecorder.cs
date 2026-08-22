using System.IO;
using System.Threading.Channels;
using Privon.Windows;

namespace Privon.App;

/// <summary>
/// Phase 3C STEP41 -- the real production <see cref="IClipboardDiagnosticRecorder"/> for manual QA:
/// appends one metadata-only line per <see cref="ClipboardDiagnosticEvent"/> to a local, temporary,
/// plain-text log file. LOCAL_ONLY (frozen for this STEP): no network call of any kind exists
/// anywhere in this type -- only <see cref="FileStream"/>/<see cref="StreamWriter"/> against a
/// local path.
///
/// PASSIVE_OBSERVER (frozen): <see cref="Record"/> is a single non-blocking
/// <see cref="ChannelWriter{T}.TryWrite"/> -- it never performs file I/O itself, never blocks the
/// calling thread (which may be the Windows clipboard owner thread, inside
/// <c>ClipboardChangeMonitor.Changed</c>'s own bounded-callback contract, or the coordinator's
/// worker thread while holding <see cref="IClipboardOperationGate"/>), and never throws (a full
/// channel silently drops the newest event rather than blocking -- <c>BoundedChannelFullMode.DropWrite</c>,
/// matching this codebase's own established "never block, coalesce/drop instead" philosophy for
/// clipboard-adjacent channels). All actual file I/O happens on a single dedicated background
/// <see cref="Task"/> that drains the channel -- entirely decoupled from whatever thread called
/// <see cref="Record"/>.
///
/// CONSTRUCTOR_FAILURE_ISOLATION (frozen): if the underlying file cannot be opened (invalid path,
/// disk full, permission denied, ...), this type does not throw -- it silently becomes a
/// file-less sink: <see cref="Record"/> still accepts events into the channel (eventually dropped,
/// never written anywhere), but construction of this type, and therefore
/// <see cref="PrivonAppComposition.Start"/> itself, is never affected by a diagnostic-sink-only
/// failure. The same isolation applies to every individual write: a failure writing one line is
/// swallowed and the background loop simply continues with the next queued event.
///
/// TEMPORARY, THIS-STEP-ONLY INSTRUMENTATION -- see <see cref="ClipboardDiagnosticStage"/>'s own
/// class doc for the full scope note.
///
/// TWO_LAYER_SINK (Phase 3C STEP41.1, extended STEP41.2): as of STEP41.1, this type ALSO
/// implements <see cref="IClipboardMonitorDiagnosticSink"/> so <see cref="PrivonAppComposition"/>
/// can route <see cref="ClipboardChangeMonitor.DiagnosticObserved"/> (the Windows-layer,
/// native-boundary event) into the SAME file as the App-layer <see cref="ClipboardDiagnosticEvent"/>
/// trace -- without ever merging the two event schemas into one type (see
/// <see cref="IClipboardMonitorDiagnosticSink"/>'s own doc for why). Phase 3C STEP41.2 adds a
/// THIRD schema, <see cref="ClipboardWriteDiagnosticEvent"/> (the guarded-write
/// sequence-attribution boundary), via <see cref="RecordWriteDiagnosticEvent"/> -- kept as its own
/// distinct schema and its own distinct line prefix for the identical reason the native-notification
/// event already has its own: conflating "windows-layer" under one merged shape would make a reader
/// guess which fields a given line's prefix implies. Every written line is prefixed <c>app|</c>,
/// <c>windows|</c>, or (as of STEP41.2) <c>write|</c> so a reader can never mistake one schema's
/// observation for another's. <see cref="RecordWindowsEvent"/>/<see cref="RecordWriteDiagnosticEvent"/>
/// both have the exact same PASSIVE_OBSERVER contract as <see cref="Record"/>: never block, never
/// throw, all actual file I/O still happens only on the single shared background writer task.
/// </summary>
internal sealed class FileClipboardDiagnosticRecorder : IClipboardDiagnosticRecorder, IClipboardMonitorDiagnosticSink, IDisposable
{
    private static readonly TimeSpan ShutdownWaitTimeout = TimeSpan.FromSeconds(2);

    // TWO_LAYER_SINK (extended Phase 3C STEP41.2 to three): a small internal wrapper carrying
    // EXACTLY ONE of the three event types -- never more than one, never none (see the three
    // private factory methods below) -- so the single shared channel/background-writer
    // infrastructure can serve IClipboardDiagnosticRecorder and IClipboardMonitorDiagnosticSink's
    // two methods without merging any of their schemas.
    private readonly struct DiagnosticLine
    {
        public readonly ClipboardDiagnosticEvent? AppEvent;
        public readonly ClipboardMonitorDiagnosticEvent? WindowsEvent;
        public readonly ClipboardWriteDiagnosticEvent? WriteEvent;

        private DiagnosticLine(
            ClipboardDiagnosticEvent? appEvent,
            ClipboardMonitorDiagnosticEvent? windowsEvent,
            ClipboardWriteDiagnosticEvent? writeEvent)
        {
            AppEvent = appEvent;
            WindowsEvent = windowsEvent;
            WriteEvent = writeEvent;
        }

        public static DiagnosticLine FromApp(ClipboardDiagnosticEvent e) => new(e, null, null);
        public static DiagnosticLine FromWindows(ClipboardMonitorDiagnosticEvent e) => new(null, e, null);
        public static DiagnosticLine FromWrite(ClipboardWriteDiagnosticEvent e) => new(null, null, e);
    }

    private readonly Channel<DiagnosticLine> _channel;
    private readonly Task _writerTask;
    private readonly StreamWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// The default, this-STEP-only diagnostic file path: <c>%TEMP%\privon-diagnostic-&lt;timestamp&gt;.log</c>
    /// -- a local, clearly-temporary path, never a persistent/registry/roaming location, per this
    /// STEP's own "no long-term history required" scope.
    /// </summary>
    public static string CreateDefaultFilePath() =>
        Path.Combine(Path.GetTempPath(), $"privon-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public FileClipboardDiagnosticRecorder(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        _channel = Channel.CreateBounded<DiagnosticLine>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        _writer = TryOpenWriter(filePath);
        _writerTask = Task.Run(RunWriterLoopAsync);
    }

    private static StreamWriter? TryOpenWriter(string filePath)
    {
        try
        {
            var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream) { AutoFlush = true };
        }
        catch
        {
            // CONSTRUCTOR_FAILURE_ISOLATION -- see this type's own class doc.
            return null;
        }
    }

    /// <summary>PASSIVE_OBSERVER -- see this type's own class doc. Never throws, never blocks.</summary>
    public void Record(ClipboardDiagnosticEvent diagnosticEvent)
    {
        _channel.Writer.TryWrite(DiagnosticLine.FromApp(diagnosticEvent));
    }

    /// <summary>TWO_LAYER_SINK / PASSIVE_OBSERVER -- see this type's own class doc. Never throws,
    /// never blocks; in particular, safe to call from the Windows clipboard owner thread inside
    /// <see cref="ClipboardChangeMonitor.DiagnosticObserved"/>'s own bounded-callback contract.</summary>
    public void RecordWindowsEvent(ClipboardMonitorDiagnosticEvent windowsEvent)
    {
        _channel.Writer.TryWrite(DiagnosticLine.FromWindows(windowsEvent));
    }

    /// <summary>Phase 3C STEP41.2 -- TWO_LAYER_SINK / PASSIVE_OBSERVER, same contract as
    /// <see cref="RecordWindowsEvent"/>: never throws, never blocks, safe to call from the Windows
    /// clipboard owner thread inside <see cref="ClipboardChangeMonitor.WriteDiagnosticObserved"/>'s
    /// own bounded-callback contract.</summary>
    public void RecordWriteDiagnosticEvent(ClipboardWriteDiagnosticEvent writeEvent)
    {
        _channel.Writer.TryWrite(DiagnosticLine.FromWrite(writeEvent));
    }

    private async Task RunWriterLoopAsync()
    {
        await foreach (var line in _channel.Reader.ReadAllAsync())
        {
            if (_writer is null) continue;

            try
            {
                // TWO_LAYER_SINK (extended STEP41.2): explicit "app|"/"windows|"/"write|" prefix --
                // never a merged/ambiguous schema (see this type's own class doc).
                if (line.AppEvent is { } appEvent)
                {
                    _writer.WriteLine($"app|{appEvent}");
                }
                else if (line.WindowsEvent is { } windowsEvent)
                {
                    _writer.WriteLine($"windows|{windowsEvent}");
                }
                else if (line.WriteEvent is { } writeEvent)
                {
                    _writer.WriteLine($"write|{writeEvent}");
                }
            }
            catch
            {
                // SINK_FAILURE_ISOLATION -- a single write failure (disk full, file locked, ...)
                // must never stop the background loop from draining later events, and must never
                // propagate anywhere near protection processing.
            }
        }
    }

    /// <summary>
    /// Best-effort, bounded shutdown -- completes the channel, waits briefly for the background
    /// writer to drain, then closes the file. Never throws: a diagnostic sink's own teardown must
    /// never be allowed to fail real application shutdown (contrast with e.g.
    /// <c>ClipboardChangeMonitor.Dispose</c>'s deliberately NON-silent DISPOSE_FAILURE_BEHAVIOR --
    /// that discipline applies to real clipboard/session-lock resources, never to this purely
    /// observational, disposable-by-design sink).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel.Writer.TryComplete();

        try
        {
            _writerTask.Wait(ShutdownWaitTimeout);
        }
        catch
        {
            // Best-effort only -- see this type's own class doc.
        }

        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // Best-effort only -- see this type's own class doc.
        }
    }
}
