using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyFences.Models;

/// <summary>
/// 決定桌面上哪些檔案要隱藏 / 取消隱藏：放進分區的桌面檔案要隱藏，拿出來的要還原。
/// 只會還原「由 MyFences 隱藏的」檔案，使用者自己設成隱藏的檔案不會被動到。
/// </summary>
public static class DesktopIconSync
{
    public sealed record Plan(IReadOnlyList<string> ToHide, IReadOnlyList<string> ToUnhide);

    /// <param name="fencedPaths">所有分區（含所有分頁）裡的項目。</param>
    /// <param name="desktopDirs">桌面資料夾（個人桌面、公用桌面）。</param>
    /// <param name="trackedHidden">之前由 MyFences 隱藏的檔案。</param>
    /// <param name="exists">檔案或資料夾是否存在。</param>
    /// <param name="isHidden">目前是否已經是隱藏屬性。</param>
    public static Plan Compute(
        IEnumerable<string> fencedPaths,
        IEnumerable<string> desktopDirs,
        IEnumerable<string> trackedHidden,
        Func<string, bool> exists,
        Func<string, bool> isHidden)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var dirs = desktopDirs.Select(NormalizeDir).ToList();
        var desired = new HashSet<string>(
            fencedPaths.Where(p => IsDirectlyIn(p, dirs) && exists(p)),
            comparer);
        var tracked = new HashSet<string>(trackedHidden, comparer);

        var toUnhide = tracked.Where(p => !desired.Contains(p)).ToList();
        // 已經是隱藏的（使用者自己藏的）就不碰，也不記錄，之後才不會被我們取消隱藏
        var toHide = desired.Where(p => !tracked.Contains(p) && !isHidden(p)).ToList();
        return new Plan(toHide, toUnhide);
    }

    /// <summary>路徑是不是直接放在某個桌面資料夾裡（不含子資料夾）。</summary>
    public static bool IsDirectlyIn(string path, IEnumerable<string> dirs)
    {
        string? parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parent == null)
            return false;

        string normalized = NormalizeDir(parent);
        return dirs.Any(d => string.Equals(NormalizeDir(d), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDir(string dir) =>
        dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
