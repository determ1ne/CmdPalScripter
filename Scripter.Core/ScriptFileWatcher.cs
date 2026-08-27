namespace Scripter.Core;

public sealed class ScriptFileWatcher : IDisposable
{
    private readonly string _scriptPath;
    private readonly string _metadataPath;
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _debounceDelay;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private bool _disposed;

    public ScriptFileWatcher(string scriptPath, TimeSpan? debounceDelay = null)
    {
        _scriptPath = Path.GetFullPath(scriptPath);
        _metadataPath = ScriptMetadataLoader.GetMetadataPath(_scriptPath);
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(250);
        var directory = Path.GetDirectoryName(_scriptPath) ?? Directory.GetCurrentDirectory();
        _watcher = new FileSystemWatcher(directory)
        {
            Filter = "*.*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    public event EventHandler<ErrorEventArgs>? Error;

    public Task WaitForChangeAsync(CancellationToken cancellationToken = default) =>
        _signal.WaitAsync(cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        _signal.Dispose();
    }

    private bool IsTarget(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(_scriptPath, StringComparison.OrdinalIgnoreCase)
            || fullPath.Equals(_metadataPath, StringComparison.OrdinalIgnoreCase);
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (IsTarget(args.FullPath))
        {
            ScheduleSignal();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        if (IsTarget(args.FullPath) || IsTarget(args.OldFullPath))
        {
            ScheduleSignal();
        }
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        Error?.Invoke(this, args);
        ScheduleSignal();
    }

    private void ScheduleSignal()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _timer ??= new System.Threading.Timer(_ => Signal(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Signal()
    {
        try
        {
            if (!_disposed && _signal.CurrentCount == 0)
            {
                _signal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
