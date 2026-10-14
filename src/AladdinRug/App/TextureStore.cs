using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AladdinRug
{
    /// <summary>A detached image and an atomic, app-owned copy. The source path is never saved.</summary>
    internal sealed class TextureStore
    {
        internal const long MaxFileBytes = 20 * 1024 * 1024;
        internal const int MaxDimension = 8192, MaxPixels = 32 * 1024 * 1024, StoredDimension = 2048;
        private readonly string _directory;
        internal string ImagePath => Path.Combine(_directory, "texture.image");
        private string ShadingPath => Path.Combine(_directory, "fold-shading.txt");
        public BitmapSource Current { get; private set; }
        public bool FoldShading { get; private set; }
        public string LoadError { get; private set; }
        public bool HasStoredImage => Current != null || File.Exists(ImagePath);

        public TextureStore(string directory)
        {
            _directory = Path.GetFullPath(directory);
            try
            {
                CheckDirectory();
                if (File.Exists(ImagePath)) { CheckFile(ImagePath); Current = ReadImage(ImagePath, out _, savedCopy: true); }
            }
            catch (Exception ex) when (IsImageError(ex))
            {
                LoadError = "The saved image could not be read. Using the built-in rug; choose another image or clear the saved copy.";
            }
            try
            {
                CheckDirectory();
                if (File.Exists(ShadingPath))
                {
                    CheckFile(ShadingPath);
                    if (new FileInfo(ShadingPath).Length > 16) throw new InvalidDataException();
                    string value = File.ReadAllText(ShadingPath).Trim();
                    if (value != "on" && value != "off") throw new InvalidDataException();
                    FoldShading = value == "on";
                }
            }
            catch (Exception ex) when (IsImageError(ex))
            {
                LoadError = (LoadError == null ? "" : LoadError + "\n") + "The image lighting setting could not be read. Using original colors.";
            }
        }

        public bool TryImport(string source, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(source)) return false; // cancelled: no reads or writes
            try
            {
                if (string.Equals(Path.GetFullPath(source), ImagePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Choose a source image outside the app's image folder.");
                BitmapSource image = ReadImage(source, out byte[] encoded); // detached, source handle closed before writing
                // Keep the validated encoded bytes, including alpha, orientation and color profiles.
                // Re-encoding premultiplied pixels can accumulate rounding differences on reload.
                WriteAtomic(ImagePath, stream => stream.Write(encoded));
                Current = image; // publish only after the durable replacement succeeds
                LoadError = null;
                return true;
            }
            catch (Exception ex) when (IsImageError(ex))
            {
                error = ex is InvalidDataException ? ex.Message : "Could not read or save the image. Check the file, free space and permissions. The previous image is unchanged.";
                return false;
            }
        }

        public bool TryClear(out string error)
        {
            error = null;
            try
            {
                CheckDirectory(); CheckFile(ImagePath);
                if (Directory.Exists(_directory)) File.Delete(ImagePath); // only our copy, never the user's source
                Current = null;
                LoadError = null;
                return true;
            }
            catch (Exception ex) when (IsImageError(ex))
            {
                error = "Could not clear the saved image. Check the image folder's permissions. The previous image is unchanged.";
                return false;
            }
        }

        public bool TrySetFoldShading(bool on, out string error)
        {
            error = null;
            try
            {
                WriteAtomic(ShadingPath, stream => stream.Write(Encoding.UTF8.GetBytes(on ? "on\n" : "off\n")));
                FoldShading = on;
                return true;
            }
            catch (Exception ex) when (IsImageError(ex))
            {
                error = "Could not save image lighting. The previous setting is unchanged.";
                return false;
            }
        }

        private void CheckDirectory()
        {
            if (Directory.Exists(_directory) && (File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The image directory must not be a link.");
        }

        private static void CheckFile(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The saved image files must not be links.");
        }

        private void WriteAtomic(string path, Action<FileStream> write)
        {
            string pending = null;
            try
            {
                CheckDirectory(); CheckFile(path);
                Directory.CreateDirectory(_directory);
                pending = Path.Combine(_directory, ".image-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (var stream = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
                pending = null;
            }
            finally
            {
                if (pending != null)
                    try { File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        internal static bool IsImageError(Exception ex) => ex is IOException || ex is InvalidDataException || ex is System.IO.FileFormatException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException ||
            ex is InvalidOperationException || ex is COMException || ex is System.Security.SecurityException || ex is OverflowException;

        internal static void ValidateDimensions(int width, int height)
        {
            if (width < 1 || height < 1 || width > MaxDimension || height > MaxDimension || (long)width * height > MaxPixels)
                throw new InvalidDataException("Images must be at most 8192 pixels per side and 32 megapixels in total.");
            if (Math.Max(width, height) > 256L * Math.Min(width, height))
                throw new InvalidDataException("The image aspect ratio must be no wider or taller than 256:1.");
        }

        private static BitmapSource ReadImage(string path, out byte[] bytes, bool savedCopy = false)
        {
            if (!Path.IsPathFullyQualified(path) || new Uri(path).IsUnc || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException("Choose an image stored on this computer.");
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (!savedCopy && extension != ".png" && extension != ".jpg" && extension != ".jpeg")
                throw new InvalidDataException("Choose a PNG or JPEG image.");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length <= 0 || file.Length > MaxFileBytes)
                    throw new InvalidDataException("Choose a non-empty image no larger than 20 MiB.");
                bytes = new byte[(int)file.Length];
                file.ReadExactly(bytes);
            }
            using var input = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
            if (decoder is not PngBitmapDecoder && decoder is not JpegBitmapDecoder)
                throw new InvalidDataException("The file contents must be PNG or JPEG.");
            BitmapFrame source = decoder.Frames[0];
            ValidateDimensions(source.PixelWidth, source.PixelHeight);
            BitmapSource frame = source;
            if (source.ColorContexts != null && source.ColorContexts.Count > 0)
                frame = new ColorConvertedBitmap(frame, source.ColorContexts[0], new ColorContext(PixelFormats.Bgra32), PixelFormats.Bgra32);

            // Normalize JPEG orientation for display; the app copy retains the original encoded metadata.
            if (decoder is JpegBitmapDecoder && source.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
            {
                var transform = new TransformGroup();
                if (orientation == 2 || orientation == 4 || orientation == 5 || orientation == 7) transform.Children.Add(new ScaleTransform(-1, 1));
                if (orientation == 3 || orientation == 4) transform.Children.Add(new RotateTransform(180));
                if (orientation == 7 || orientation == 6) transform.Children.Add(new RotateTransform(90));
                if (orientation == 5 || orientation == 8) transform.Children.Add(new RotateTransform(270));
                if (transform.Children.Count > 0) frame = new TransformedBitmap(frame, transform);
            }
            int orientedWidth = frame.PixelWidth, orientedHeight = frame.PixelHeight;
            double scale = Math.Min(1.0, StoredDimension / (double)Math.Max(frame.PixelWidth, frame.PixelHeight));
            if (scale < 1) frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            int stride = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0); // force full decode before committing a corrupt file
            // Separate DPI ratios retain the exact source aspect even when the smaller pixel side rounds.
            var detached = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
                96.0 * converted.PixelWidth / orientedWidth, 96.0 * converted.PixelHeight / orientedHeight, PixelFormats.Pbgra32, null, pixels, stride);
            detached.Freeze();
            return detached;
        }
    }
}
