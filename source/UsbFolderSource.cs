using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace GoW3Installer
{
    internal sealed class UsbFolderSource : IFolderImportSource
    {
        readonly StorageFolder root;
        internal UsbFolderSource(StorageFolder root) { this.root = root; }

        public async Task<long?> TryGetSizeAsync(string relativePath)
        {
            var file = await TryGetFileAsync(relativePath);
            if (file == null) return null;
            return (long)(await file.GetBasicPropertiesAsync()).Size;
        }

        public async Task<Stream> OpenReadAsync(string relativePath)
        {
            var file = await TryGetFileAsync(relativePath);
            if (file == null) throw new FileNotFoundException("Missing file in selected folder: " + relativePath);
            return await file.OpenStreamForReadAsync();
        }

        async Task<StorageFile> TryGetFileAsync(string relativePath)
        {
            StorageFolder folder = root;
            string[] parts = relativePath.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                folder = (await folder.TryGetItemAsync(parts[i])) as StorageFolder;
                if (folder == null) return null;
            }
            return (await folder.TryGetItemAsync(parts[parts.Length - 1])) as StorageFile;
        }
    }
}
