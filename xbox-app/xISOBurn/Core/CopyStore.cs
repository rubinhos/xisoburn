using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace xISOBurn.Core
{
    internal sealed class StoreEntry
    {
        public string Name;
        public long Size;
    }

    /// <summary>
    /// Where the copies (.iso, .map, .sha256) are written: the app's own storage, or a folder on a USB
    /// drive. The app storage uses System.IO, as before. On USB the app only has the WinRT StorageFolder
    /// API (removableStorage capability), and only sees the file types declared in Package.appxmanifest:
    /// .iso, .map, .sha256 and .log. Temporary files therefore keep one of those extensions.
    ///
    /// The setting (CopyConfig.OutputFolder) is "" for the app storage, or "E:\folder\sub" on a USB
    /// drive (the drive's root path, then the folders inside it).
    /// </summary>
    internal abstract class CopyStore
    {
        public abstract string Description { get; }
        public abstract bool IsUsb { get; }

        /// <summary>Opens or creates a file for reading and writing; others may read it at the same time.</summary>
        public abstract Task<Stream> OpenWriteAsync(string name);
        /// <summary>Opens a file for reading (shared with a writer); null if it does not exist.</summary>
        public abstract Task<Stream> OpenReadAsync(string name);
        public abstract Task<long> SizeAsync(string name);  // -1 if missing
        public abstract Task DeleteAsync(string name);
        /// <summary>Renames 'from' to 'to', replacing 'to' if it exists.</summary>
        public abstract Task ReplaceAsync(string from, string to);
        public abstract Task<List<StoreEntry>> ListAsync(string extension);
        public abstract Task<long> FreeBytesAsync();         // -1 if unknown

        public async Task<bool> ExistsAsync(string name) { return await SizeAsync(name) >= 0; }

        public async Task<string> ReadTextAsync(string name)
        {
            using (var s = await OpenReadAsync(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8)) return await r.ReadToEndAsync();
            }
        }

        /// <summary>Writes to a temporary file, then replaces 'name' with it (a crash never leaves half a file).</summary>
        public async Task WriteTextAtomicAsync(string name, string text)
        {
            string tmp = Path.GetFileNameWithoutExtension(name) + ".tmp" + Path.GetExtension(name);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var s = await OpenWriteAsync(tmp))
            {
                s.SetLength(0);
                await s.WriteAsync(bytes, 0, bytes.Length);
                await s.FlushAsync();
            }
            await ReplaceAsync(tmp, name);
        }

        /// <summary>The store for a setting; throws with a readable message if the USB folder is not there.</summary>
        public static async Task<CopyStore> OpenAsync(string outputFolder, string appFolder)
        {
            if (string.IsNullOrWhiteSpace(outputFolder)) return new LocalStore(appFolder);
            var folder = await UsbBrowser.ResolveAsync(outputFolder);
            if (folder == null)
                throw new IOException("The destination " + outputFolder + " is not available: is the USB drive plugged in? " +
                                      "Choose the destination again in Settings.");
            return new UsbStore(folder, outputFolder);
        }

        public static string FormatBytes(long bytes)
        {
            return bytes < 0 ? "?" : (bytes / 1073741824.0).ToString("0.0") + " GiB";
        }
    }

    /// <summary>The app's own storage (LocalState), with System.IO.</summary>
    internal sealed class LocalStore : CopyStore
    {
        private readonly string _folder;

        public LocalStore(string folder) { _folder = folder; }

        public override string Description { get { return "the Xbox (app storage)"; } }
        public override bool IsUsb { get { return false; } }

        private string P(string name) { return Path.Combine(_folder, name); }

        public override Task<Stream> OpenWriteAsync(string name)
        {
            // bufferSize 1 = no .NET-level buffering: every write reaches the OS at once, so the web server
            // (another stream on the same file) never sends stale zeros for a sector already marked Ok.
            return Task.FromResult<Stream>(new FileStream(P(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1));
        }

        public override Task<Stream> OpenReadAsync(string name)
        {
            string p = P(name);
            if (!File.Exists(p)) return Task.FromResult<Stream>(null);
            return Task.FromResult<Stream>(new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16));
        }

        public override Task<long> SizeAsync(string name)
        {
            var fi = new FileInfo(P(name));
            return Task.FromResult(fi.Exists ? fi.Length : -1L);
        }

        public override Task DeleteAsync(string name)
        {
            if (File.Exists(P(name))) File.Delete(P(name));
            return Task.FromResult(0);
        }

        public override Task ReplaceAsync(string from, string to)
        {
            if (File.Exists(P(to))) File.Delete(P(to));
            File.Move(P(from), P(to));
            return Task.FromResult(0);
        }

        public override Task<List<StoreEntry>> ListAsync(string extension)
        {
            var list = new List<StoreEntry>();
            foreach (var f in Directory.GetFiles(_folder, "*" + extension))
                list.Add(new StoreEntry { Name = Path.GetFileName(f), Size = new FileInfo(f).Length });
            return Task.FromResult(list);
        }

        public override async Task<long> FreeBytesAsync()
        {
            try
            {
                var props = await ApplicationData.Current.LocalFolder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                object v;
                return props.TryGetValue("System.FreeSpace", out v) && v is ulong ? (long)(ulong)v : -1;
            }
            catch (Exception) { return -1; }
        }
    }

    /// <summary>A folder on a USB drive, with the WinRT StorageFolder API.</summary>
    internal sealed class UsbStore : CopyStore
    {
        private readonly StorageFolder _folder;
        private readonly string _setting;

        public UsbStore(StorageFolder folder, string setting)
        {
            _folder = folder;
            _setting = setting;
        }

        public override string Description { get { return "USB " + _setting; } }
        public override bool IsUsb { get { return true; } }

        private async Task<StorageFile> FileAsync(string name)
        {
            var item = await _folder.TryGetItemAsync(name);
            return item as StorageFile;
        }

        public override async Task<Stream> OpenWriteAsync(string name)
        {
            var file = await _folder.CreateFileAsync(name, CreationCollisionOption.OpenIfExists);
            var ras = await file.OpenAsync(FileAccessMode.ReadWrite, StorageOpenOptions.AllowReadersAndWriters);
            return ras.AsStream(0); // unbuffered, as for the app storage
        }

        public override async Task<Stream> OpenReadAsync(string name)
        {
            var file = await FileAsync(name);
            if (file == null) return null;
            var ras = await file.OpenAsync(FileAccessMode.Read, StorageOpenOptions.AllowReadersAndWriters);
            return ras.AsStream(1 << 16);
        }

        public override async Task<long> SizeAsync(string name)
        {
            var file = await FileAsync(name);
            if (file == null) return -1;
            var props = await file.GetBasicPropertiesAsync();
            return (long)props.Size;
        }

        public override async Task DeleteAsync(string name)
        {
            var file = await FileAsync(name);
            if (file != null) await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }

        public override async Task ReplaceAsync(string from, string to)
        {
            var file = await FileAsync(from);
            if (file == null) throw new FileNotFoundException(from);
            await file.RenameAsync(to, NameCollisionOption.ReplaceExisting);
        }

        public override async Task<List<StoreEntry>> ListAsync(string extension)
        {
            var list = new List<StoreEntry>();
            foreach (var f in await _folder.GetFilesAsync())
            {
                if (!f.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                var props = await f.GetBasicPropertiesAsync();
                list.Add(new StoreEntry { Name = f.Name, Size = (long)props.Size });
            }
            return list;
        }

        public override Task<long> FreeBytesAsync() { return UsbBrowser.FreeBytesAsync(_folder); }
    }

    /// <summary>Browsing the USB drives: drive list, subfolders, new folders; turns a setting into a folder.</summary>
    internal static class UsbBrowser
    {
        internal sealed class Entry
        {
            public string Path;     // the setting value for this folder ("E:\a\b")
            public string Label;
        }

        private static string RootOf(StorageFolder drive)
        {
            string p = drive.Path;
            if (string.IsNullOrEmpty(p)) p = drive.DisplayName;
            return p.TrimEnd('\\') + "\\";
        }

        public static async Task<List<Entry>> DrivesAsync()
        {
            var list = new List<Entry>();
            foreach (var d in await KnownFolders.RemovableDevices.GetFoldersAsync())
            {
                long free = await FreeBytesAsync(d);
                list.Add(new Entry { Path = RootOf(d), Label = d.DisplayName + " (" + RootOf(d) + ", " + CopyStore.FormatBytes(free) + " free)" });
            }
            return list;
        }

        /// <summary>The folder for a setting such as "E:\copies\bd", or null.</summary>
        public static async Task<StorageFolder> ResolveAsync(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting)) return null;
            string s = setting.Replace('/', '\\');
            foreach (var d in await KnownFolders.RemovableDevices.GetFoldersAsync())
            {
                string root = RootOf(d);
                if (!(s + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                StorageFolder f = d;
                foreach (var part in s.Substring(Math.Min(s.Length, root.Length)).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = await f.TryGetItemAsync(part) as StorageFolder;
                    if (next == null) return null;
                    f = next;
                }
                return f;
            }
            return null;
        }

        public static async Task<List<Entry>> SubfoldersAsync(string setting)
        {
            var list = new List<Entry>();
            var f = await ResolveAsync(setting);
            if (f == null) return null;
            string basePath = setting.Replace('/', '\\').TrimEnd('\\');
            var folders = new List<StorageFolder>(await f.GetFoldersAsync());
            folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            foreach (var sub in folders)
                list.Add(new Entry { Path = basePath + "\\" + sub.Name, Label = sub.Name });
            return list;
        }

        /// <summary>Creates 'name' inside the folder of 'setting'; returns the new setting value.</summary>
        public static async Task<string> CreateFolderAsync(string setting, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Invalid folder name.");
            var f = await ResolveAsync(setting);
            if (f == null) throw new IOException("The folder " + setting + " is not available.");
            await f.CreateFolderAsync(name, CreationCollisionOption.OpenIfExists);
            return setting.Replace('/', '\\').TrimEnd('\\') + "\\" + name;
        }

        /// <summary>Parent of a setting; "" when at a drive root (back to the drive list).</summary>
        public static string Parent(string setting)
        {
            string s = setting.Replace('/', '\\').TrimEnd('\\');
            int i = s.LastIndexOf('\\');
            if (i < 0 || s.IndexOf('\\') == i && s.EndsWith(":")) return "";
            string parent = s.Substring(0, i);
            return parent.EndsWith(":") ? parent + "\\" : parent;
        }

        public static async Task<long> FreeBytesAsync(StorageFolder folder)
        {
            try
            {
                var props = await folder.Properties.RetrievePropertiesAsync(new[] { "System.FreeSpace" });
                object v;
                return props.TryGetValue("System.FreeSpace", out v) && v is ulong ? (long)(ulong)v : -1;
            }
            catch (Exception) { return -1; }
        }
    }
}
