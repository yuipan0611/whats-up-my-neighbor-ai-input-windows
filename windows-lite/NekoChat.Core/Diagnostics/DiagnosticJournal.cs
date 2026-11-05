using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace NekoChat.Core.Diagnostics;

public sealed record DiagnosticEvent(long Sequence, DateTime Utc, string Session, string Version, DiagnosticStage Stage,
    DiagnosticOutcome Outcome, DiagnosticCode Code, DiagnosticCategory Category, DiagnosticSeverity Severity, DiagnosticOrigin Origin,
    string? MessageId, int Attempt, long? ElapsedMs, long Count, string? ExceptionType, string[] Frames);
public sealed record DiagnosticExportResult(bool Success, DiagnosticCode Code);
public sealed record DiagnosticJournalOptions
{
    public int FileBytes { get; init; } = 512 * 1024;
    public int FileCount { get; init; } = 4;
    public int QueueCapacity { get; init; } = 256;
    public int RecentCapacity { get; init; } = 256;
}

/// <summary>Metadata only. Callers never perform disk I/O or wait for the bounded writer queue.</summary>
public sealed class DiagnosticJournal : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<string, byte> Owners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private sealed record Work(DiagnosticEvent? Event = null, string? ExportPath = null, TaskCompletionSource<DiagnosticExportResult>? Export = null, CancellationToken Cancellation = default);
    private readonly string _directory, _version, _session = Guid.NewGuid().ToString("D");
    private readonly DiagnosticJournalOptions _options;
    private readonly Channel<Work> _pending;
    private readonly Queue<DiagnosticEvent> _recent = new();
    private readonly Dictionary<(DiagnosticStage, DiagnosticOutcome, DiagnosticCode, string?, int, string?, long), long> _repeat = new();
    private readonly object _gate = new();
    private readonly Task _writer;
    private long _sequence, _dropped, _writeFailures, _suppressed;
    private int _stopping, _persistent;
    public long DroppedEvents => Interlocked.Read(ref _dropped);
    public long SuppressedEvents => Interlocked.Read(ref _suppressed);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);
    public bool PersistentAvailable => Volatile.Read(ref _persistent) == 1;
    public DiagnosticEvent[] Recent { get { lock (_gate) return _recent.ToArray(); } }

    public DiagnosticJournal(string directory, string version, DiagnosticJournalOptions? options = null)
    {
        _directory = Path.GetFullPath(directory); _options = options ?? new();
        if (_options.FileBytes is < 16384 or > 1048576 || _options.FileCount is < 1 or > 8 ||
            _options.QueueCapacity is < 1 or > 4096 || _options.RecentCapacity is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        _version = version.Length is > 0 and <= 48 && version.All(c => char.IsAsciiDigit(c) || c is '.' or '-' or '+' || c is >= 'a' and <= 'z') ? version : "unknown";
        _pending = Channel.CreateBounded<Work>(new BoundedChannelOptions(_options.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = Task.Run(WriteLoopAsync);
        Record(DiagnosticStage.Journal, DiagnosticOutcome.Started);
    }
    public void Record(DiagnosticStage stage, DiagnosticOutcome outcome, DiagnosticCode code = DiagnosticCode.None,
        Exception? error = null, string? messageId = null, int attempt = 0, long? elapsedMs = null, long count = 0, bool cancellationRequested = false, DiagnosticOrigin origin = DiagnosticOrigin.Local)
    {
        if (Volatile.Read(ref _stopping) != 0) return;
        try
        {
            var fault = code != DiagnosticCode.None ? DiagnosticFault.For(code) : error is null ? DiagnosticFault.For(code) : DiagnosticFault.FromException(error, cancellationRequested);
            string? id = Guid.TryParseExact(messageId, "D", out var parsed) || Guid.TryParseExact(messageId, "N", out parsed) ? parsed.ToString("D") : null;
            var item = new DiagnosticEvent(Interlocked.Increment(ref _sequence), DateTime.UtcNow, _session, _version, stage, outcome,
                fault.Code, fault.Category, fault.Severity, origin, id, Math.Max(0, attempt), elapsedMs is { } elapsed ? Math.Clamp(elapsed, 0, 604800000) : null, Math.Max(0, count), SafeExceptionType(error), SafeFrames(error));
            lock (_gate)
            {
                var key = (stage, outcome, fault.Code, id, attempt, item.Frames.FirstOrDefault(), item.Count);
                long now = Stopwatch.GetTimestamp();
                if (_repeat.TryGetValue(key, out var previous) && Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(2)) { Interlocked.Increment(ref _suppressed); return; }
                if (_repeat.Count >= 128) _repeat.Clear(); _repeat[key] = now;
                while (_recent.Count >= _options.RecentCapacity) _recent.Dequeue(); _recent.Enqueue(item);
            }
            if (!_pending.Writer.TryWrite(new(Event: item))) Interlocked.Increment(ref _dropped);
        }
        catch (Exception errorWriting) when (DiagnosticFault.Catchable(errorWriting)) { Interlocked.Increment(ref _dropped); }
    }
    public async Task<DiagnosticExportResult> ExportAsync(string destination, CancellationToken cancellation = default)
    {
        try
        {
            string full = Path.GetFullPath(destination);
            if (full.StartsWith(_directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return new(false, DiagnosticCode.InvalidPayload);
            var done = new TaskCompletionSource<DiagnosticExportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(5));
            if (!_pending.Writer.TryWrite(new(ExportPath: full, Export: done, Cancellation: deadline.Token))) return new(false, DiagnosticCode.QueueFull);
            return await done.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(false, cancellation.IsCancellationRequested ? DiagnosticCode.Cancelled : DiagnosticCode.Timeout); }
        catch (Exception error) when (DiagnosticFault.Catchable(error)) { return new(false, DiagnosticFault.FromException(error).Code); }
    }
    private string FileAt(int index) => Path.Combine(_directory, "diagnostics-" + index + ".jsonl");
    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null ||
            (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Diagnostic location is not a regular local file.");
    }
    private void Rotate()
    {
        for (int i = 0; i < _options.FileCount; i++) RejectLink(FileAt(i));
        File.Delete(FileAt(_options.FileCount - 1));
        for (int i = _options.FileCount - 2; i >= 0; i--) if (File.Exists(FileAt(i))) File.Move(FileAt(i), FileAt(i + 1));
    }
    private async Task WriteLoopAsync()
    {
        FileStream? owner = null; bool disk = false, registered = false;
        try
        {
            try
            {
                RejectLink(_directory); Directory.CreateDirectory(_directory); RejectLink(Path.Combine(_directory, "writer.lock"));
                if (!Owners.TryAdd(_directory, 0)) throw new IOException("Diagnostic writer already active.");
                registered = true;
                owner = new(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Rotate(); disk = true;
            }
            catch (Exception error) when (DiagnosticFault.Catchable(error)) { Interlocked.Increment(ref _writeFailures); }
            await foreach (var work in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (work.Event is { } item && disk)
                {
                    try
                    {
                        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(item, Json) + "\n");
                        if (line.Length > _options.FileBytes) { Interlocked.Increment(ref _dropped); continue; }
                        RejectLink(_directory); RejectLink(FileAt(0));
                        if (File.Exists(FileAt(0)) && new FileInfo(FileAt(0)).Length + line.Length > _options.FileBytes) Rotate();
                        await using var file = new FileStream(FileAt(0), FileMode.Append, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                        await file.WriteAsync(line).ConfigureAwait(false);
                        Volatile.Write(ref _persistent, 1);
                    }
                    catch (Exception error) when (DiagnosticFault.Catchable(error))
                    { disk = false; Volatile.Write(ref _persistent, 0); Interlocked.Increment(ref _writeFailures); }
                }
                if (work.Export is { } export)
                {
                    if (work.Cancellation.IsCancellationRequested) { export.TrySetResult(new(false, DiagnosticCode.Cancelled)); continue; }
                    try { Export(work.ExportPath!, disk, work.Cancellation); export.TrySetResult(new(true, DiagnosticCode.None)); }
                    catch (OperationCanceledException) { export.TrySetResult(new(false, DiagnosticCode.Cancelled)); }
                    catch (Exception error) when (DiagnosticFault.Catchable(error)) { export.TrySetResult(new(false, DiagnosticFault.FromException(error).Code)); }
                }
            }
        }
        finally { owner?.Dispose(); if (registered) Owners.TryRemove(_directory, out _); Volatile.Write(ref _persistent, 0); }
    }
    private void Export(string destination, bool disk, CancellationToken cancellation)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                if (disk)
                {
                    RejectLink(_directory);
                    for (int i = 0; i < _options.FileCount; i++)
                    {
                        cancellation.ThrowIfCancellationRequested(); string source = FileAt(i); RejectLink(source);
                        if (!File.Exists(source)) continue;
                        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (input.Length > _options.FileBytes) throw new IOException("Diagnostic file exceeds retention bound.");
                        using var entry = zip.CreateEntry("diagnostics-" + i + ".jsonl").Open(); input.CopyTo(entry);
                    }
                }
                using (var text = new StreamWriter(zip.CreateEntry("recent.jsonl").Open(), new UTF8Encoding(false)))
                    foreach (var item in Recent) text.WriteLine(JsonSerializer.Serialize(item, Json));
                using var summary = new StreamWriter(zip.CreateEntry("summary.json").Open(), new UTF8Encoding(false));
                summary.Write(JsonSerializer.Serialize(new { format = 1, version = _version, session = _session, persistent = disk, droppedEvents = DroppedEvents, writeFailures = WriteFailures, suppressedEvents = SuppressedEvents }));
            }
            cancellation.ThrowIfCancellationRequested(); File.Move(temporary, destination, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static string? SafeExceptionType(Exception? error)
    {
        if (error is null) return null;
        return error switch {
            NekoChat.Core.Storage.StoreException => "NekoChat.Core.Storage.StoreException",
            NekoChat.Core.Attachments.AttachmentException => "NekoChat.Core.Attachments.AttachmentException",
            StorageWorkQueueFullException => "NekoChat.Core.Diagnostics.StorageWorkQueueFullException",
            Microsoft.Data.Sqlite.SqliteException => "Microsoft.Data.Sqlite.SqliteException",
            System.Net.Sockets.SocketException => "System.Net.Sockets.SocketException",
            HttpRequestException => "System.Net.Http.HttpRequestException",
            OperationCanceledException => "System.OperationCanceledException",
            TimeoutException => "System.TimeoutException",
            UnauthorizedAccessException => "System.UnauthorizedAccessException",
            IOException => "System.IO.IOException",
            ArgumentException => "System.ArgumentException",
            InvalidOperationException => "System.InvalidOperationException",
            NullReferenceException => "System.NullReferenceException",
            _ => "OtherException"
        };
    }
    private static string[] SafeFrames(Exception? error)
    {
        var frames = new List<string>();
        for (int depth = 0; error is not null && depth < 3; depth++, error = error.InnerException)
            foreach (var frame in new StackTrace(error, false).GetFrames())
            {
                MethodBase? method = frame.GetMethod();
                string? assembly = method?.Module.Assembly.GetName().Name;
                if (assembly is not ("NekoChat.Core" or "NekoChat.Windows" or "NekoChat.Tests")) continue;
                Type? declaring = method?.DeclaringType;
                string? type = (declaring?.IsGenericType == true ? declaring.GetGenericTypeDefinition() : declaring)?.FullName;
                if (type is null || !type.StartsWith("NekoChat.", StringComparison.Ordinal)) continue;
                string token = new string((type + "." + method!.Name).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+' or '<' or '>').Take(160).ToArray());
                frames.Add(token); if (frames.Count == 8) return frames.ToArray();
            }
        return frames.ToArray();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0) _pending.Writer.TryComplete();
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (Exception error) when (DiagnosticFault.Catchable(error)) { Interlocked.Increment(ref _writeFailures); }
    }
}
