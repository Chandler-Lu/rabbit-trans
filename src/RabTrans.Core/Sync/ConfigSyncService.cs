using System.IO.Compression;
using RabTrans.Core.Storage;

namespace RabTrans.Core.Sync;

public class ConfigSyncService
{
    private readonly StorageService _storageService;

    public ConfigSyncService(StorageService storageService)
    {
        _storageService = storageService;
    }

    public void CreateConfigPackage(string packagePath, bool includeHistory)
    {
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        AddFileToArchive(archive, _storageService.SettingsPath, "settings.json");
        AddDirectoryToArchive(archive, Path.Combine(_storageService.AppDataPath, "plugins"), "plugins");

        if (includeHistory)
        {
            AddFileToArchive(archive, _storageService.HistoryPath, "history.jsonl");
        }
    }

    public void ImportConfigPackage(string packagePath)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"RabTransImport-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(packagePath, tempDir);
            CopyIfExists(Path.Combine(tempDir, "settings.json"), _storageService.SettingsPath);
            ReplaceDirectory(Path.Combine(tempDir, "plugins"), Path.Combine(_storageService.AppDataPath, "plugins"));
            CopyIfExists(Path.Combine(tempDir, "history.jsonl"), _storageService.HistoryPath);
            _storageService.Reload();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        if (File.Exists(filePath))
        {
            archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
        }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string directoryPath, string entryPrefix)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            archive.CreateEntryFromFile(filePath, $"{entryPrefix}/{relativePath}", CompressionLevel.Optimal);
        }
    }

    private static void CopyIfExists(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? ".");
        File.Copy(sourcePath, targetPath, true);
    }

    private static void ReplaceDirectory(string sourceDirectory, string targetDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, true);
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            var targetPath = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDirectory);
            File.Copy(sourcePath, targetPath, true);
        }
    }
}
