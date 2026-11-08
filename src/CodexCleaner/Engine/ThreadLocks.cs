namespace CodexCleaner.Engine;

// Codex keeps thread-writer-locks/<id>.lock under an exclusive LockFileEx range lock for every
// thread it has loaded, and serializes creating, probing and removing those files through
// .coordination.lock (codex-rs/thread-store/src/local/writer_lock.rs). Following the same
// protocol lets us see which chats are in use and stop Codex from opening one mid-deletion.
internal static class ThreadLocks
{
    public const string Directory = "thread-writer-locks";
    private const string Coordination = ".coordination.lock";
    private const int LockViolation = unchecked((int)0x80070021);
    private static readonly TimeSpan CoordinationWait = TimeSpan.FromSeconds(5);

    public static HashSet<string> Held(string directory, IEnumerable<string> ids)
    {
        var held = new HashSet<string>(StringComparer.Ordinal);
        if (!System.IO.Directory.Exists(directory)) return held;
        using var coordination = LockCoordination(directory);
        foreach (var id in ids)
        {
            var file = Path.Combine(directory, id + ".lock");
            if (!File.Exists(file)) continue;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (TryLock(stream)) stream.Unlock(0, long.MaxValue); else held.Add(id);
            }
            catch (FileNotFoundException) { }
        }
        return held;
    }

    // Locks are taken in ordinal order, as Codex does for batch deletion.
    public static Guard Acquire(string directory, IEnumerable<string> ids)
    {
        System.IO.Directory.CreateDirectory(directory);
        var guard = new Guard(directory);
        try
        {
            using var coordination = LockCoordination(directory);
            foreach (var id in ids.Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(directory, id + ".lock");
                var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                if (!TryLock(stream))
                {
                    stream.Dispose();
                    throw new CleanerException($"聊天 {id} 刚刚在 Codex 中被打开，请关闭它后重新预览。");
                }
                guard.Add(file, stream);
            }
        }
        catch
        {
            guard.Dispose();
            throw;
        }
        return guard;
    }

    // Rust's File::try_lock is LockFileEx over the whole file; any overlapping range conflicts.
    public static bool TryLock(FileStream stream)
    {
        try { stream.Lock(0, long.MaxValue); return true; }
        catch (IOException e) when (e.HResult == LockViolation) { return false; }
    }

    public static void Wait(FileStream stream, string busyMessage)
    {
        var deadline = DateTime.UtcNow + CoordinationWait;
        while (!TryLock(stream))
        {
            if (DateTime.UtcNow > deadline) throw new CleanerException(busyMessage);
            Thread.Sleep(20);
        }
    }

    private static FileStream LockCoordination(string directory)
    {
        var stream = new FileStream(Path.Combine(directory, Coordination), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        try { Wait(stream, "Codex 正在切换聊天，暂时无法确认哪些聊天在使用，请稍后重试。"); }
        catch { stream.Dispose(); throw; }
        return stream;
    }

    public sealed class Guard(string directory) : IDisposable
    {
        private readonly List<(string File, FileStream Stream)> held = [];

        public void Add(string file, FileStream stream) => held.Add((file, stream));

        // Mirrors WriterLockGuard::drop: close each lock before removing it, under coordination.
        public void Dispose()
        {
            if (held.Count == 0) return;
            FileStream? coordination = null;
            try { coordination = LockCoordination(directory); }
            catch (CleanerException) { }
            foreach (var (file, stream) in held)
            {
                stream.Dispose();
                if (coordination is null) continue;
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            held.Clear();
            coordination?.Dispose();
        }
    }
}
