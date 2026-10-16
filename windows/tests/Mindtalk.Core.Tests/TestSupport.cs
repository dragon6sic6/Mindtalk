namespace Mindtalk.Core.Tests;

/// <summary>Tests that switch <see cref="Loc.IsSwedish"/> (a process-wide setting) run alone.</summary>
[CollectionDefinition("Loc", DisableParallelization = true)]
public sealed class LocCollection;

/// <summary>Runs in one language, then puts the language back.</summary>
public sealed class Language : IDisposable
{
    private readonly bool _was = Loc.IsSwedish;

    private Language(bool swedish) => Loc.IsSwedish = swedish;

    public static Language Swedish() => new(true);
    public static Language English() => new(false);

    public void Dispose() => Loc.IsSwedish = _was;
}

/// <summary>A folder of its own under %TEMP%, removed afterwards — never the real %APPDATA%.</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mindtalk-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
