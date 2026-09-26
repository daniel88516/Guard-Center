using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;
using MediaImageSource = System.Windows.Media.ImageSource;

namespace GuardCenter
{
    internal static class ApplicationIconService
    {
        internal const string CustomPngFileName = "CustomAppIcon.png";
        internal const string CustomIcoFileName = "CustomAppIcon.ico";
        private const string TempSuffix = ".tmp";

        public static string Import(string sourcePath)
        {
            return Import(sourcePath, AppPaths.AppearanceRoot);
        }

        internal static string Import(string sourcePath, string appearanceRoot)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("The selected image was not found.", sourcePath);
            }

            string root = Path.GetFullPath(appearanceRoot);
            Directory.CreateDirectory(root);
            CleanupTemporaryAndOrphanFiles(root);

            string pngPath = Path.Combine(root, CustomPngFileName);
            string icoPath = Path.Combine(root, CustomIcoFileName);
            string pngTemp = pngPath + TempSuffix;
            string icoTemp = icoPath + TempSuffix;

            try
            {
                using (Bitmap source = LoadSourceBitmap(sourcePath))
                using (Bitmap square = RenderSquare(source, 512))
                {
                    square.Save(pngTemp, ImageFormat.Png);
                    WriteMultiSizeIcon(square, icoTemp);
                }

                ValidateOutput(pngTemp, icoTemp);
                File.Move(pngTemp, pngPath, true);
                File.Move(icoTemp, icoPath, true);
                CleanupTemporaryAndOrphanFiles(root);
                return pngPath;
            }
            finally
            {
                DeleteIfExists(pngTemp);
                DeleteIfExists(icoTemp);
            }
        }

        public static void Reset()
        {
            Reset(AppPaths.AppearanceRoot);
        }

        internal static void Reset(string appearanceRoot)
        {
            string root = Path.GetFullPath(appearanceRoot);
            if (!Directory.Exists(root))
            {
                return;
            }

            DeleteIfExists(Path.Combine(root, CustomPngFileName));
            DeleteIfExists(Path.Combine(root, CustomIcoFileName));
            CleanupTemporaryAndOrphanFiles(root);
            if (Directory.GetFileSystemEntries(root).Length == 0)
            {
                Directory.Delete(root, false);
            }
        }

        public static string NormalizeCustomPath(string configuredPath)
        {
            string normalized = NormalizeExistingFile(configuredPath);
            return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
        }

        public static string GetShellIconPath(string customPngPath)
        {
            if (!string.IsNullOrWhiteSpace(NormalizeExistingFile(customPngPath)))
            {
                string icoPath = Path.Combine(Path.GetDirectoryName(customPngPath) ?? string.Empty,
                    CustomIcoFileName);
                if (File.Exists(icoPath))
                {
                    return Path.GetFullPath(icoPath);
                }
            }

            return AppPaths.InstalledExePath;
        }

        public static DrawingIcon LoadDrawingIcon(string customPngPath)
        {
            string custom = NormalizeExistingFile(customPngPath);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                string icoPath = Path.Combine(Path.GetDirectoryName(custom) ?? string.Empty,
                    CustomIcoFileName);
                try
                {
                    if (File.Exists(icoPath))
                    {
                        using (var icon = new DrawingIcon(icoPath, new Size(64, 64)))
                        {
                            return (DrawingIcon)icon.Clone();
                        }
                    }

                    using (var bitmap = new Bitmap(custom))
                    using (var resized = RenderSquare(bitmap, 64))
                    {
                        IntPtr handle = resized.GetHicon();
                        try
                        {
                            using (DrawingIcon icon = DrawingIcon.FromHandle(handle))
                            {
                                return (DrawingIcon)icon.Clone();
                            }
                        }
                        finally
                        {
                            DestroyIcon(handle);
                        }
                    }
                }
                catch
                {
                }
            }

            try
            {
                string path = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    using (DrawingIcon extracted = DrawingIcon.ExtractAssociatedIcon(path))
                    {
                        if (extracted != null)
                        {
                            return (DrawingIcon)extracted.Clone();
                        }
                    }
                }
            }
            catch
            {
            }

            return (DrawingIcon)SystemIcons.Shield.Clone();
        }

        public static MediaImageSource LoadImageSource(string customPngPath)
        {
            string custom = NormalizeExistingFile(customPngPath);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                try
                {
                    using (var stream = new FileStream(custom, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        if (bitmap.CanFreeze)
                        {
                            bitmap.Freeze();
                        }
                        return bitmap;
                    }
                }
                catch
                {
                }
            }

            using (DrawingIcon icon = LoadDrawingIcon(string.Empty))
            {
                BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                if (source.CanFreeze)
                {
                    source.Freeze();
                }
                return source;
            }
        }

        public static void CleanupManagedOrphans()
        {
            CleanupTemporaryAndOrphanFiles(AppPaths.AppearanceRoot);
        }

        internal static List<string> GetManagedFiles(string appearanceRoot)
        {
            var result = new List<string>();
            if (!Directory.Exists(appearanceRoot))
            {
                return result;
            }

            string[] files = Directory.GetFiles(appearanceRoot, "CustomAppIcon*", SearchOption.TopDirectoryOnly);
            result.AddRange(files);
            return result;
        }

        private static Bitmap LoadSourceBitmap(string sourcePath)
        {
            if (string.Equals(Path.GetExtension(sourcePath), ".ico", StringComparison.OrdinalIgnoreCase))
            {
                using (var icon = new DrawingIcon(sourcePath, new Size(256, 256)))
                {
                    return icon.ToBitmap();
                }
            }

            using (Image loaded = Image.FromFile(sourcePath, true))
            {
                return new Bitmap(loaded);
            }
        }

        private static Bitmap RenderSquare(Image source, int size)
        {
            if (source == null || source.Width <= 0 || source.Height <= 0)
            {
                throw new InvalidDataException("The selected file is not a usable image.");
            }

            int side = Math.Min(source.Width, source.Height);
            var sourceRectangle = new Rectangle((source.Width - side) / 2,
                (source.Height - side) / 2, side, side);
            var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.Clear(Color.Transparent);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, size, size), sourceRectangle, GraphicsUnit.Pixel);
            }
            return target;
        }

        private static void WriteMultiSizeIcon(Bitmap source, string path)
        {
            int[] sizes = new int[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var frames = new List<byte[]>();
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap frame = RenderSquare(source, sizes[i]))
                using (var stream = new MemoryStream())
                {
                    frame.Save(stream, ImageFormat.Png);
                    frames.Add(stream.ToArray());
                }
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)frames.Count);
                int offset = 6 + frames.Count * 16;
                for (int i = 0; i < frames.Count; i++)
                {
                    int size = sizes[i];
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)(size == 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)frames[i].Length);
                    writer.Write((uint)offset);
                    offset += frames[i].Length;
                }
                for (int i = 0; i < frames.Count; i++)
                {
                    writer.Write(frames[i]);
                }
            }
        }

        private static void ValidateOutput(string pngPath, string icoPath)
        {
            using (var bitmap = new Bitmap(pngPath))
            {
                if (bitmap.Width != 512 || bitmap.Height != 512)
                {
                    throw new InvalidDataException("The generated application icon has an invalid size.");
                }
            }
            using (var icon = new DrawingIcon(icoPath, new Size(32, 32)))
            {
                if (icon.Width <= 0 || icon.Height <= 0)
                {
                    throw new InvalidDataException("The generated Windows icon is invalid.");
                }
            }
        }

        private static void CleanupTemporaryAndOrphanFiles(string appearanceRoot)
        {
            if (!Directory.Exists(appearanceRoot))
            {
                return;
            }

            string pngPath = Path.GetFullPath(Path.Combine(appearanceRoot, CustomPngFileName));
            string icoPath = Path.GetFullPath(Path.Combine(appearanceRoot, CustomIcoFileName));
            string[] files = Directory.GetFiles(appearanceRoot, "CustomAppIcon*", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string fullPath = Path.GetFullPath(files[i]);
                if (!string.Equals(fullPath, pngPath, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(fullPath, icoPath, StringComparison.OrdinalIgnoreCase))
                {
                    DeleteIfExists(fullPath);
                }
            }
        }

        private static string NormalizeExistingFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            try
            {
                string fullPath = Path.GetFullPath(path);
                return File.Exists(fullPath) ? fullPath : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void DeleteIfExists(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
