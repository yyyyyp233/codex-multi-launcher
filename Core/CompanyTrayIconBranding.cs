using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;

namespace CodexChannelLauncher.Core;

public static class CompanyTrayIconBranding
{
    public const int CurrentVersion = 1;

    private static readonly string[] ManagedRelativePaths =
    [
        Path.Combine("resources", "chatgpt-tray-dark.ico"),
        Path.Combine("resources", "chatgpt-tray-light.ico")
    ];

    private static readonly int[] IconSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static bool IsManagedRelativePath(string relativePath) =>
        ManagedRelativePaths.Any(path =>
            path.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

    public static TrayBrandingResult Apply(
        string sourceApp,
        string targetApp,
        LauncherLog? log = null)
    {
        var missing = ManagedRelativePaths
            .Where(relative => !File.Exists(Path.Combine(sourceApp, relative)))
            .ToArray();
        if (missing.Length > 0)
        {
            RestoreOfficialIcons(sourceApp, targetApp);
            var fallbackHashes = CaptureHashes(targetApp);
            var detail = $"当前 Store 包缺少托盘图标资源：{string.Join(", ", missing)}；工作空间 App 保留官方图标。";
            log?.Info($"Company tray branding skipped: {detail}");
            return new TrayBrandingResult(false, CurrentVersion, fallbackHashes, detail);
        }

        var pending = new List<(string RelativePath, string TemporaryPath, string TargetPath)>();
        try
        {
            foreach (var relativePath in ManagedRelativePaths)
            {
                var sourcePath = Path.Combine(sourceApp, relativePath);
                var targetPath = Path.Combine(targetApp, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var temporaryPath = targetPath + $".company-branding-{Guid.NewGuid():N}.tmp";
                pending.Add((relativePath, temporaryPath, targetPath));
                WriteBrandedIcon(sourcePath, temporaryPath);
                ValidateReadableIcon(temporaryPath);
            }

            foreach (var item in pending)
            {
                File.Move(item.TemporaryPath, item.TargetPath, true);
            }

            var hashes = CaptureHashes(targetApp);
            var result = new TrayBrandingResult(
                true,
                CurrentVersion,
                hashes,
                "工作空间托盘图标已叠加紫色右下角角标。");
            log?.Info($"Company tray branding applied: version={CurrentVersion}, files={hashes.Count}");
            return result;
        }
        catch (Exception exception)
        {
            foreach (var item in pending)
            {
                TryDelete(item.TemporaryPath);
            }

            try
            {
                RestoreOfficialIcons(sourceApp, targetApp);
            }
            catch (Exception restoreException)
            {
                throw new IOException(
                    "工作空间托盘角标生成失败，并且无法恢复官方托盘图标。",
                    new AggregateException(exception, restoreException));
            }

            log?.Error("Company tray branding failed; official tray icons restored", exception);
            return new TrayBrandingResult(
                false,
                CurrentVersion,
                CaptureHashes(targetApp),
                $"角标生成失败，已安全保留官方图标：{exception.GetBaseException().Message}");
        }
        finally
        {
            foreach (var item in pending)
            {
                TryDelete(item.TemporaryPath);
            }
        }
    }

    public static bool Validate(
        string sourceApp,
        string targetApp,
        RuntimeCacheManifest manifest)
    {
        if (manifest.CompanyTrayBrandingVersion != CurrentVersion ||
            manifest.CompanyTrayIconSha256 is null)
        {
            return false;
        }

        var actualFileCount = 0;
        foreach (var relativePath in ManagedRelativePaths)
        {
            var sourceExists = File.Exists(Path.Combine(sourceApp, relativePath));
            var targetPath = Path.Combine(targetApp, relativePath);
            var targetExists = File.Exists(targetPath);
            if (sourceExists != targetExists)
            {
                return false;
            }

            if (!targetExists)
            {
                continue;
            }

            actualFileCount++;
            if (!manifest.CompanyTrayIconSha256.TryGetValue(relativePath, out var expectedHash) ||
                !ComputeSha256(targetPath).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return manifest.CompanyTrayIconSha256.Count == actualFileCount &&
               (!manifest.CompanyTrayBrandingApplied || actualFileCount == ManagedRelativePaths.Length);
    }

    public static string VerifyCurrentPackageIcons(string sourceApp)
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "CodexChannelLauncher",
            "tray-branding-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(testRoot);
            var result = Apply(sourceApp, testRoot);
            if (!result.Applied || result.FileSha256.Count != ManagedRelativePaths.Length)
            {
                throw new InvalidOperationException(result.Detail);
            }

            foreach (var relativePath in ManagedRelativePaths)
            {
                ValidateReadableIcon(Path.Combine(testRoot, relativePath));
            }

            var validationManifest = new RuntimeCacheManifest(
                "branding-test",
                sourceApp,
                0,
                0,
                "ChatGPT.exe",
                string.Empty,
                DateTime.UtcNow,
                result.Version,
                result.Applied,
                result.FileSha256);
            if (!Validate(sourceApp, testRoot, validationManifest))
            {
                throw new InvalidDataException("工作空间角标托盘图标哈希或清单校验失败。");
            }

            return $"已从当前 Store 包生成并读取 {result.FileSha256.Count} 个工作空间角标托盘图标。";
        }
        finally
        {
            try
            {
                Directory.Delete(testRoot, true);
            }
            catch
            {
                // A temporary verification artifact is harmless and contains no credentials.
            }
        }
    }

    private static void WriteBrandedIcon(string sourcePath, string destinationPath)
    {
        var images = IconSizes
            .Select(size => RenderFrame(sourcePath, size))
            .ToArray();

        using var stream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Length);

        var offset = 6 + 16 * images.Length;
        for (var index = 0; index < images.Length; index++)
        {
            var size = IconSizes[index];
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)(size == 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)images[index].Length);
            writer.Write((uint)offset);
            offset += images[index].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }
    }

    private static byte[] RenderFrame(string sourcePath, int size)
    {
        using var sourceIcon = LoadIcon(sourcePath, size);
        using var sourceBitmap = sourceIcon.ToBitmap();
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawImage(sourceBitmap, new Rectangle(0, 0, size, size));
            DrawBadge(graphics, size);
        }

        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static Icon LoadIcon(string path, int size)
    {
        try
        {
            return new Icon(path, size, size);
        }
        catch
        {
            return new Icon(path);
        }
    }

    private static void DrawBadge(Graphics graphics, int size)
    {
        var diameter = Math.Max(6, (int)Math.Round(size * 0.40));
        var margin = Math.Max(0, (int)Math.Round(size * 0.025));
        var inset = Math.Max(1, (int)Math.Round(size * 0.055));
        var outer = new Rectangle(size - diameter - margin, size - diameter - margin, diameter, diameter);
        var inner = Rectangle.Inflate(outer, -inset, -inset);

        using var shadowBrush = new SolidBrush(Color.FromArgb(110, 15, 18, 28));
        using var borderBrush = new SolidBrush(Color.FromArgb(255, 246, 243, 255));
        using var badgeBrush = new SolidBrush(Color.FromArgb(255, 124, 58, 237));

        var shadow = outer;
        shadow.Offset(Math.Max(1, size / 64), Math.Max(1, size / 64));
        graphics.FillEllipse(shadowBrush, shadow);
        graphics.FillEllipse(borderBrush, outer);
        graphics.FillEllipse(badgeBrush, inner);
    }

    private static void RestoreOfficialIcons(string sourceApp, string targetApp)
    {
        foreach (var relativePath in ManagedRelativePaths)
        {
            var sourcePath = Path.Combine(sourceApp, relativePath);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var targetPath = Path.Combine(targetApp, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, true);
        }
    }

    private static Dictionary<string, string> CaptureHashes(string targetApp)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in ManagedRelativePaths)
        {
            var targetPath = Path.Combine(targetApp, relativePath);
            if (File.Exists(targetPath))
            {
                hashes[relativePath] = ComputeSha256(targetPath);
            }
        }

        return hashes;
    }

    private static void ValidateReadableIcon(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
        {
            throw new InvalidDataException($"托盘图标头无效：{path}");
        }

        var count = reader.ReadUInt16();
        if (count != IconSizes.Length)
        {
            throw new InvalidDataException($"托盘图标尺寸数量无效：{path}");
        }

        var entries = new List<(int Size, uint Length, uint Offset)>(count);
        for (var index = 0; index < count; index++)
        {
            var width = reader.ReadByte();
            var height = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            var length = reader.ReadUInt32();
            var offset = reader.ReadUInt32();
            var decodedWidth = width == 0 ? 256 : width;
            var decodedHeight = height == 0 ? 256 : height;
            if (decodedWidth != decodedHeight || decodedWidth != IconSizes[index])
            {
                throw new InvalidDataException($"托盘图标尺寸目录无效：{path}");
            }

            entries.Add((decodedWidth, length, offset));
        }

        foreach (var entry in entries)
        {
            if (entry.Length == 0 || entry.Offset + entry.Length > stream.Length)
            {
                throw new InvalidDataException($"托盘图标帧范围无效：{path}");
            }

            stream.Position = entry.Offset;
            var bytes = reader.ReadBytes(checked((int)entry.Length));
            using var imageStream = new MemoryStream(bytes, false);
            using var image = Image.FromStream(imageStream, true, true);
            if (image.Width != entry.Size || image.Height != entry.Size)
            {
                throw new InvalidDataException($"托盘图标帧尺寸无效：{path}");
            }
        }

        using var icon = new Icon(path);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The next cache validation will reject an incomplete temporary artifact if one remains.
        }
    }
}
