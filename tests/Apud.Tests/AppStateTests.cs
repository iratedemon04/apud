using Apud.App;

namespace Apud.Tests;

/// <summary>
/// The one remembered piece of UI state (user 2026-08-01, explicit exception):
/// the last folder a File dialog used. Missing/corrupt file → blank, never a crash.
/// </summary>
public class AppStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"apud-ui-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    [Fact]
    public void Round_trips_the_last_folder()
    {
        new AppState { LastFolder = @"C:\Some\Where" }.SaveTo(_path);
        Assert.Equal(@"C:\Some\Where", AppState.LoadFrom(_path).LastFolder);
    }

    [Fact]
    public void A_missing_file_loads_blank()
    {
        Assert.Null(AppState.LoadFrom(_path).LastFolder); // never written
    }

    [Fact]
    public void A_corrupt_file_loads_blank_without_throwing()
    {
        File.WriteAllText(_path, "{ this is not json");
        Assert.Null(AppState.LoadFrom(_path).LastFolder);
    }

    [Fact]
    public void Round_trips_the_last_catalogue()
    {
        new AppState { LastCatalogue = @"C:\cat\catalog.db" }.SaveTo(_path);
        Assert.Equal(@"C:\cat\catalog.db", AppState.LoadFrom(_path).LastCatalogue);
        Assert.Null(AppState.LoadFrom(_path + ".missing").LastCatalogue); // absent → null, no reopen
    }

    [Fact]
    public void Round_trips_the_first_run_flag()
    {
        new AppState { FirstRunDone = true }.SaveTo(_path);
        Assert.True(AppState.LoadFrom(_path).FirstRunDone);
    }

    [Fact]
    public void First_run_flag_defaults_false_when_absent()
    {
        Assert.False(AppState.LoadFrom(_path).FirstRunDone);              // missing file
        File.WriteAllText(_path, "{ this is not json");
        Assert.False(AppState.LoadFrom(_path).FirstRunDone);              // corrupt file
    }
}
