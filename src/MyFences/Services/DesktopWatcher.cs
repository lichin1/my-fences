using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Threading;

namespace MyFences.Services;

/// <summary>監看桌面資料夾的新增與改名，事件會回到 UI 執行緒。</summary>
internal sealed class DesktopWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();

    public DesktopWatcher(Dispatcher dispatcher, Action<string> created, Action<string, string> renamed)
    {
        foreach (string dir in DesktopHelper.GetDesktopDirectories())
        {
            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += (_, e) => dispatcher.InvokeAsync(() => created(e.FullPath));
                watcher.Renamed += (_, e) => dispatcher.InvokeAsync(() => renamed(e.OldFullPath, e.FullPath));
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
