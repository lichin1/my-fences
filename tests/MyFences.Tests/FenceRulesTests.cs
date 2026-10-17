using MyFences.Models;
using Xunit;

namespace MyFences.Tests;

public sealed class FenceRulesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "myfences-tests-" + Guid.NewGuid().ToString("N"));
    private readonly DateTime _now = new(2026, 10, 17, 12, 0, 0);

    public FenceRulesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string MakeFile(string name, int sizeBytes = 10, int daysAgo = 0)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[sizeBytes]);
        File.SetLastWriteTime(path, _now.AddDays(-daysAgo));
        return path;
    }

    private string MakeFolder(string name)
    {
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private bool Matches(string path, params string[] rules) => FenceRules.Matches(rules, path, _now);

    [Theory]
    [InlineData("pdf")]
    [InlineData(".pdf")]
    [InlineData(".PDF")]
    public void Extension_matches_with_or_without_dot(string rule)
    {
        Assert.True(Matches(MakeFile("a.pdf"), rule));
        Assert.False(Matches(MakeFile("a.docx"), rule));
    }

    [Fact]
    public void Folder_and_star_rules()
    {
        string folder = MakeFolder("工作");
        string file = MakeFile("x.txt");

        Assert.True(Matches(folder, "folder"));
        Assert.False(Matches(file, "folder"));
        Assert.True(Matches(file, "*"));
        Assert.False(Matches(folder, "*"));
        Assert.False(Matches(folder, "txt"));
    }

    [Fact]
    public void Name_contains_and_wildcards()
    {
        string file = MakeFile("2024 月報表.xlsx");

        Assert.True(Matches(file, "name:報表"));
        Assert.True(Matches(file, "name:月報"));
        Assert.False(Matches(file, "name:發票"));
        Assert.True(Matches(file, "name:2024*.xlsx"));
        Assert.False(Matches(file, "name:2025*"));
        Assert.True(Matches(MakeFolder("報表資料夾"), "name:報表"));
    }

    [Fact]
    public void Newer_and_older_use_last_write_time()
    {
        string recent = MakeFile("recent.txt", daysAgo: 3);
        string old = MakeFile("old.txt", daysAgo: 45);

        Assert.True(Matches(recent, "newer:7"));
        Assert.False(Matches(old, "newer:7"));
        Assert.True(Matches(old, "older:30"));
        Assert.False(Matches(recent, "older:30"));
    }

    [Fact]
    public void Larger_and_smaller_in_megabytes_skip_folders()
    {
        string big = MakeFile("big.bin", sizeBytes: 2 * 1024 * 1024);
        string small = MakeFile("small.bin", sizeBytes: 100);

        Assert.True(Matches(big, "larger:1"));
        Assert.False(Matches(small, "larger:1"));
        Assert.True(Matches(small, "smaller:0.5"));
        Assert.False(Matches(big, "smaller:0.5"));
        Assert.False(Matches(MakeFolder("dir"), "smaller:1"));
    }

    [Fact]
    public void Plus_means_and_comma_list_means_or()
    {
        string invoice = MakeFile("發票-10月.pdf");
        string manual = MakeFile("說明書.pdf");

        Assert.True(Matches(invoice, "pdf+name:發票"));
        Assert.False(Matches(manual, "pdf+name:發票"));
        Assert.True(Matches(manual, "pdf+name:發票", "name:說明"));
        Assert.False(Matches(invoice, "docx", "name:報表"));
    }

    [Fact]
    public void Empty_rules_match_nothing()
    {
        Assert.False(Matches(MakeFile("a.txt")));
        Assert.False(Matches(MakeFile("b.txt"), " ", "+"));
    }

    [Fact]
    public void Invalid_conditions_are_reported()
    {
        Assert.Empty(FenceRules.FindInvalidConditions(new[] { "pdf", ".lnk", "folder", "*", "name:報表", "newer:7+larger:1.5" }));
        Assert.Equal(new[] { "newer:abc", "size:5", "older:" },
            FenceRules.FindInvalidConditions(new[] { "pdf+newer:abc", "size:5", "older:" }));
    }
}
