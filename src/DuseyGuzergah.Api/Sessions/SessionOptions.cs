namespace DuseyGuzergah.Api.Sessions;

public sealed class DesignSessionOptions
{
    public const string SectionName = "Sessions";

    public string RootPath { get; set; } = "";

    public int TtlMinutes { get; set; } = 120;

    public long MaxUploadBytes { get; set; } = 52_428_800;
}
