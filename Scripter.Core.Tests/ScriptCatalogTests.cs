using Scripter.Core;

namespace Scripter.Core.Tests;

public sealed class ScriptCatalogTests
{
    [Fact]
    public async Task CatalogDebouncesScriptAndMetadataChanges()
    {
        using var directory = new TemporaryDirectory();
        using var catalog = new ScriptCatalog(directory.Path, TimeSpan.FromMilliseconds(75));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var changeCount = 0;
        catalog.Changed += (_, _) =>
        {
            Interlocked.Increment(ref changeCount);
            changed.TrySetResult();
        };

        var path = Path.Combine(directory.Path, "watched.js");
        File.WriteAllText(path, "1");
        File.WriteAllText(path, "2");
        File.WriteAllText(path + ".meta.json", "{ \"name\": \"Watched\" }");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        Assert.Equal(1, Volatile.Read(ref changeCount));
        var entry = Assert.Single(catalog.Entries);
        Assert.Equal("Watched", entry.Metadata.Name);
    }

    [Fact]
    public async Task FileWatcherSignalsForTheSelectedScriptAndSidecar()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "selected.js");
        File.WriteAllText(path, "1");
        using var watcher = new ScriptFileWatcher(path, TimeSpan.FromMilliseconds(75));

        var change = watcher.WaitForChangeAsync();
        File.WriteAllText(path, "2");
        File.WriteAllText(path + ".meta.json", "{}");

        await change.WaitAsync(TimeSpan.FromSeconds(5));
        using var noDuplicate = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watcher.WaitForChangeAsync(noDuplicate.Token));
    }
}
