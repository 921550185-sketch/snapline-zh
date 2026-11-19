using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace Snapline.Core;

public static class Library
{
    public static bool Owns(string path) => Settings.Current.ManagedPaths.Contains(Path.GetFullPath(path), StringComparer.OrdinalIgnoreCase);

    public static string Store(BitmapSource image)
    {
        var path = Path.Combine(Inbox.Folder, $"图片-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.png");
        var pending = path + ".tmp";
        try
        {
            Thumbnails.SavePng(image, pending);
            File.Move(pending, path);
            Settings.Current.ManagedPaths.Add(path);
            if (!Settings.Current.Save()) throw new IOException("无法保存图片清单，请检查保存位置和磁盘空间。");
            return path;
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    public static string Import(string path)
    {
        path = Path.GetFullPath(path);
        if (Owns(path)) return path;
        var image = Thumbnails.Load(path, int.MaxValue) ?? throw new IOException("图片尚未写入完成或格式不受支持");
        var copy = Store(Thumbnails.NormalizeClipboard(image.Image));
        if (Settings.Current.Notes.TryGetValue(path, out var note)) Settings.Current.Notes[copy] = note;
        return copy;
    }

    public static string Fingerprint(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    public static void Forget(string path)
    {
        Settings.Current.ManagedPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
    }

    public static void ChangeFolder(string destination, Line line)
    {
        destination = Path.GetFullPath(destination);
        var oldFolder = Path.GetFullPath(Inbox.Folder);
        if (destination.Equals(oldFolder, StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetPathRoot(destination)?.TrimEnd('\\') == destination.TrimEnd('\\')) throw new IOException("请选择专门存放贴图的文件夹，而不是磁盘根目录。");
        Directory.CreateDirectory(destination);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Settings.Current.ManagedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var target = Inbox.UniquePath(destination, Path.GetFileName(path));
            File.Copy(path, target);
            if (!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(target))))
                throw new IOException("图片迁移校验失败，仍使用原保存位置。");
            map[path] = target;
            if (Originals.Exists(path))
            {
                Directory.CreateDirectory(Originals.Folder);
                File.Copy(Originals.BackupPath(path), Originals.BackupPath(target), true);
            }
        }
        var s = Settings.Current;
        var oldManaged = s.ManagedPaths; var oldPegged = s.Pegged; var oldPinned = s.PinnedPaths;
        var oldSpots = s.Spots; var oldNotes = s.Notes; var oldInbox = s.InboxFolder;
        var oldItemPaths = line.Items.ToDictionary(i => i.Id, i => i.Path);
        s.ManagedPaths = s.ManagedPaths.Select(p => map.GetValueOrDefault(p, p)).ToList();
        s.Pegged = s.Pegged.Select(p => map.GetValueOrDefault(p, p)).ToList();
        s.PinnedPaths = s.PinnedPaths.Select(p => map.GetValueOrDefault(p, p)).ToList();
        s.Spots = s.Spots.ToDictionary(p => map.GetValueOrDefault(p.Key, p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        s.Notes = s.Notes.ToDictionary(p => map.GetValueOrDefault(p.Key, p.Key), p => p.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var item in line.Items) item.Path = map.GetValueOrDefault(item.Path, item.Path);
        s.InboxFolder = destination;
        if (!s.Save())
        {
            s.ManagedPaths = oldManaged; s.Pegged = oldPegged; s.PinnedPaths = oldPinned;
            s.Spots = oldSpots; s.Notes = oldNotes; s.InboxFolder = oldInbox;
            foreach (var item in line.Items) item.Path = oldItemPaths[item.Id];
            throw new IOException("无法保存新路径，仍使用原保存位置。");
        }
        line.Save();
        foreach (var item in line.Items.ToList()) line.ReloadThumbnail(item.Path);
        foreach (var path in map.Keys)
            try { File.Delete(path); Originals.Forget(path); } catch (Exception e) { Log.Error($"原文件已迁移但旧副本无法清理: {e.Message}"); }
    }
}
