using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;

namespace MyFences.Services;

/// <summary>監看桌面資料夾的新增、改名與刪除，事件會回到 UI 執行緒。</summary>
internal sealed class DesktopWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();

    public DesktopWatcher(Dispatcher dispatcher, Action<string> created, Action<string, string> renamed, Action<string> deleted)
    {
        foreach (string dir in DesktopHelper.GetDesktopDirectories())
        {
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                    InternalBufferSize = 64 * 1024,
                };
                // 屬性改變（例如設成隱藏）也會影響桌面上看不看得到
                watcher.Changed += (_, e) => dispatcher.InvokeAsync(() => deleted(e.FullPath));
                watcher.Created += (_, e) => dispatcher.InvokeAsync(() => created(e.FullPath));
                watcher.Renamed += (_, e) => dispatcher.InvokeAsync(() => renamed(e.OldFullPath, e.FullPath));
                watcher.Deleted += (_, e) => dispatcher.InvokeAsync(() => deleted(e.FullPath));
                // 一次大量變動時事件會滿出來：當作整個桌面都要重新讀
                watcher.Error += (_, _) => dispatcher.InvokeAsync(() => deleted(dir));
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception)
            {
                // 例如沒有權限監看公用桌面，略過即可
            }
        }
    }

    public void Dispose()
    {
        foreach (FileSystemWatcher watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}
