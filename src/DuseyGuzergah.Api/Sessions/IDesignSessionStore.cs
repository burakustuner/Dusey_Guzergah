namespace DuseyGuzergah.Api.Sessions;

public interface IDesignSessionStore
{
    string RootPath { get; }

    string CreateSessionDirectory(Guid id);

    void Add(DesignSession session);

    DesignSession? TryGet(Guid id);

    void Abandon(Guid id);
}
