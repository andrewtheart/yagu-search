using QuickGrep.Services;

namespace QuickGrep.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void RoundTrip_Persists()
    {
        var temp = Path.Combine(Path.GetTempPath(), "qg-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(temp);
            var s = new AppSettings
            {
                LastDirectory = @"D:\proj",
                CaseSensitive = true,
                UseRegex = true,
                ContextLines = 7,
                EditorCommand = "code --goto \"{file}:{line}\"",
            };
            s.RecentDirectories.Add(@"D:\a");
            s.SearchHistory.Add("foo");
            svc.Save(s);

            var loaded = svc.Load();
            Assert.Equal(@"D:\proj", loaded.LastDirectory);
            Assert.True(loaded.CaseSensitive);
            Assert.True(loaded.UseRegex);
            Assert.Equal(7, loaded.ContextLines);
            Assert.Single(loaded.RecentDirectories);
            Assert.Single(loaded.SearchHistory);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    [Fact]
    public void PushRecent_DeduplicatesAndCapsSize()
    {
        var list = new List<string> { "a", "b", "c" };
        SettingsService.PushRecent(list, "b");
        Assert.Equal(new[] { "b", "a", "c" }, list);

        for (int i = 0; i < 25; i++) SettingsService.PushRecent(list, $"item{i}", 5);
        Assert.Equal(5, list.Count);
    }
}
