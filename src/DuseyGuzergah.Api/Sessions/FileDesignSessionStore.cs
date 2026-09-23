using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace DuseyGuzergah.Api.Sessions;

public sealed class FileDesignSessionStore : IDesignSessionStore
{
    private readonly ConcurrentDictionary<Guid, DesignSession> _sessions = new();
    private readonly TimeSpan _ttl;

    public FileDesignSessionStore(IOptions<DesignSessionOptions> options)
    {
        var value = options.Value;
        _ttl = TimeSpan.FromMinutes(Math.Max(1, value.TtlMinutes));
        RootPath = string.IsNullOrWhiteSpace(value.RootPath)
            ? Path.Combine(Path.GetTempPath(), "dusey-guzergah-sessions")
            : value.RootPath;
        Directory.CreateDirectory(RootPath);
        PurgeStaleDirectories();
    }

    public string RootPath { get; }

    public string CreateSessionDirectory(Guid id)
    {
        var path = SessionDirectory(id);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Add(DesignSession session)
    {
        session.LastAccessUtc = DateTimeOffset.UtcNow;
        _sessions[session.Id] = session;
        PurgeExpired();
    }

    public DesignSession? TryGet(Guid id)
    {
        PurgeExpired();
        if (!_sessions.TryGetValue(id, out var session))
        {
            return null;
        }

        if (DateTimeOffset.UtcNow - session.LastAccessUtc > _ttl)
        {
            Abandon(id);
            return null;
        }

        session.LastAccessUtc = DateTimeOffset.UtcNow;
        return session;
    }

    public void Abandon(Guid id)
    {
        _sessions.TryRemove(id, out _);
        TryDelete(SessionDirectory(id));
    }

    private string SessionDirectory(Guid id) => Path.Combine(RootPath, id.ToString("N"));

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.LastAccessUtc > _ttl && _sessions.TryRemove(pair.Key, out var removed))
            {
                TryDelete(removed.DirectoryPath);
            }
        }
    }

    private void PurgeStaleDirectories()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - _ttl;
        foreach (var dir in Directory.EnumerateDirectories(RootPath))
        {
            if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
            {
                TryDelete(dir);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
