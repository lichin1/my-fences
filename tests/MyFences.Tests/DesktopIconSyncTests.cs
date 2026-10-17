using MyFences.Models;
using Xunit;

namespace MyFences.Tests;

public sealed class DesktopIconSyncTests
{
    private static readonly string Desktop = Path.Combine(Path.GetTempPath(), "Desktop");
    private static string D(string name) => Path.Combine(Desktop, name);

    private static DesktopIconSync.Plan Compute(
        string[] fenced, string[] tracked, string[]? missing = null, string[]? alreadyHidden = null) =>
        DesktopIconSync.Compute(
            fenced,
            new[] { Desktop },
            tracked,
            p => missing == null || !missing.Contains(p, StringComparer.OrdinalIgnoreCase),
            p => alreadyHidden != null && alreadyHidden.Contains(p, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Fenced_desktop_items_get_hidden()
    {
        var plan = Compute(new[] { D("a.pdf"), D("工作") }, Array.Empty<string>());

        Assert.Equal(new[] { D("a.pdf"), D("工作") }, plan.ToHide.OrderBy(p => p));
        Assert.Empty(plan.ToUnhide);
    }

    [Fact]
    public void Items_outside_the_desktop_are_never_hidden()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), "Documents", "b.pdf");
        string inSubfolder = Path.Combine(Desktop, "工作", "c.pdf");

        var plan = Compute(new[] { elsewhere, inSubfolder }, Array.Empty<string>());

        Assert.Empty(plan.ToHide);
    }

    [Fact]
    public void Items_taken_out_of_fences_get_unhidden()
    {
        var plan = Compute(new[] { D("keep.pdf") }, new[] { D("keep.pdf"), D("removed.pdf") });

        Assert.Empty(plan.ToHide);
        Assert.Equal(new[] { D("removed.pdf") }, plan.ToUnhide);
    }

    [Fact]
    public void Files_the_user_hid_themselves_are_left_alone()
    {
        var plan = Compute(new[] { D("secret.txt") }, Array.Empty<string>(), alreadyHidden: new[] { D("secret.txt") });

        Assert.Empty(plan.ToHide);
        Assert.Empty(plan.ToUnhide);
    }

    [Fact]
    public void Missing_files_are_not_hidden_and_paths_compare_case_insensitively()
    {
        var plan = Compute(new[] { D("gone.pdf"), D("A.PDF") }, new[] { D("a.pdf") }, missing: new[] { D("gone.pdf") });

        Assert.Empty(plan.ToHide);
        Assert.Empty(plan.ToUnhide);
    }

    [Fact]
    public void Turning_the_feature_off_unhides_everything_tracked()
    {
        var plan = Compute(Array.Empty<string>(), new[] { D("a.pdf"), D("b.pdf") });

        Assert.Equal(2, plan.ToUnhide.Count);
    }
}
