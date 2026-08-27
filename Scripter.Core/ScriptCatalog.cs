namespace Scripter.Core;

public sealed class ScriptCatalog : IDisposable
{
    private readonly object _gate = new();
    private readonly ScriptStorageService _storage;
    private readonly FileSystemWatcher _watcher;
    private readonly TimeSpan _debounceDelay;
    private System.Threading.Timer? _debounceTimer;
    private IReadOnlyList<ScriptFileEntry> _entries = [];
    private bool _disposed;

    public ScriptCatalog(string rootDirectory, TimeSpan? debounceDelay = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        _storage = new ScriptStorageService(RootDirectory);
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(250);
        _watcher = new FileSystemWatcher(RootDirectory)
        {
            Filter = "*.*",
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
        Scan(notify: false);
        _watcher.EnableRaisingEvents = true;
    }

    public string RootDirectory { get; }

    public IReadOnlyList<ScriptFileEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries;
            }
        }
    }

    public event EventHandler? Changed;

    public event EventHandler<ErrorEventArgs>? WatcherError;

    public void Scan(bool notify = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entries = _storage.GetScriptEntries();
        lock (_gate)
        {
            _entries = entries;
        }

        if (notify)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

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
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private static bool IsRelevantPath(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".js.meta.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        if (IsRelevantPath(args.FullPath))
        {
            ScheduleScan();
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        if (IsRelevantPath(args.FullPath) || IsRelevantPath(args.OldFullPath))
        {
            ScheduleScan();
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs args)
    {
        WatcherError?.Invoke(this, args);
        ScheduleScan();
    }

    private void ScheduleScan()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer ??= new System.Threading.Timer(_ => DebouncedScan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void DebouncedScan()
    {
        try
        {
            Scan();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException exception)
        {
            WatcherError?.Invoke(this, new ErrorEventArgs(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            WatcherError?.Invoke(this, new ErrorEventArgs(exception));
        }
    }
}
