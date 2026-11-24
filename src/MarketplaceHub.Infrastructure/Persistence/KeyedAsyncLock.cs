namespace MarketplaceHub.Infrastructure.Persistence;

// One lock per key, held only while someone holds or waits for it: the entry leaves the dictionary when its last user
// leaves, so the dictionary is as large as the keys in use, not every key ever asked. Two users of one key never run
// side by side: a new user of a key in use joins its entry, and an entry is removed (under the same monitor) only when no
// user holds or waits for it.
internal sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    internal int Count
    {
        get { lock (entries) return entries.Count; }
    }

    internal async Task<IDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (entries)
        {
            if (!entries.TryGetValue(key, out entry!)) entries[key] = entry = new Entry();
            entry.Users++;
        }
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    private void Leave(string key, Entry entry)
    {
        lock (entries)
        {
            if (--entry.Users == 0) entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int Users;
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Gate.Release();
            owner.Leave(key, entry);
        }
    }
}
