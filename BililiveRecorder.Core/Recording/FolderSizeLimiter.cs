using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace BililiveRecorder.Core.Recording
{
    /// <summary>
    /// 录制文件夹容量限制器。
    /// 当文件夹总大小超过配置的上限时，按创建时间从早到晚删除录制视频文件，
    /// 直到总大小降到限制以内。
    /// </summary>
    internal static class FolderSizeLimiter
    {
        /// <summary>
        /// 会被视为"录制视频"而可以被删除的文件扩展名。
        /// 弹幕、封面等文件不会被删除，但会计入文件夹总大小。
        /// </summary>
        private static readonly string[] VideoFileExtensions = new[] { ".flv" };

        /// <summary>
        /// 检查文件夹总大小，超出限制时删除最早的录制视频文件。
        /// </summary>
        /// <param name="folder">要检查的文件夹</param>
        /// <param name="maxBytes">大小上限（字节）</param>
        /// <param name="logger">日志</param>
        /// <returns>实际删除的文件数量</returns>
        public static int EnforceLimit(string folder, long maxBytes, ILogger logger)
        {
            if (string.IsNullOrEmpty(folder) || maxBytes <= 0 || !Directory.Exists(folder))
                return 0;

            var files = EnumerateFilesSafe(folder, logger).ToList();
            if (files.Count == 0)
                return 0;

            long totalSize = 0;
            foreach (var file in files)
            {
                try { totalSize += file.Length; }
                catch (Exception) { }
            }

            if (totalSize <= maxBytes)
                return 0;

            var excess = totalSize - maxBytes;
            logger.Information(
                "录制文件夹 {Folder} 当前大小 {TotalMB:F1} MB，超过限制 {LimitMB:F1} MB，开始删除最早的录制文件",
                folder, totalSize / 1024d / 1024d, maxBytes / 1024d / 1024d);

            var deletedCount = 0;

            // 按创建时间从早到晚排序，只删除视频文件
            var videoFiles = files
                .Where(IsVideoFile)
                .OrderBy(GetFileTime)
                .ToList();

            foreach (var file in videoFiles)
            {
                long size;
                try { size = file.Length; }
                catch (Exception) { continue; }

                try
                {
                    file.Delete();
                    deletedCount++;
                    excess -= size;
                    logger.Information("已删除最早的录制文件 {File}（{SizeMB:F1} MB）",
                        file.FullName, size / 1024d / 1024d);
                }
                catch (Exception ex)
                {
                    // 正在录制中的文件会被操作系统锁定，跳过即可
                    logger.Debug(ex, "删除文件 {File} 失败，跳过", file.FullName);
                }

                if (excess <= 0)
                    break;
            }

            if (excess > 0)
            {
                logger.Warning(
                    "录制文件夹 {Folder} 中的录制文件已全部尝试删除，但总大小仍超出限制约 {ExcessMB:F1} MB（可能因弹幕等非视频文件占用，或文件被占用无法删除）",
                    folder, excess / 1024d / 1024d);
            }

            return deletedCount;
        }

        private static IEnumerable<FileInfo> EnumerateFilesSafe(string folder, ILogger logger)
        {
            var enumerator = default(IEnumerator<FileInfo>);
            try
            {
                enumerator = new DirectoryInfo(folder)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .GetEnumerator();

                while (true)
                {
                    FileInfo current;
                    try
                    {
                        if (!enumerator.MoveNext())
                            break;
                        current = enumerator.Current;
                    }
                    catch (Exception ex)
                    {
                        logger.Debug(ex, "枚举录制文件夹 {Folder} 中的文件时出错，跳过无法访问的部分", folder);
                        continue;
                    }

                    yield return current;
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }

        private static bool IsVideoFile(FileInfo file) =>
            VideoFileExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase);

        private static DateTime GetFileTime(FileInfo file)
        {
            try
            {
                var creation = file.CreationTimeUtc;
                var write = file.LastWriteTimeUtc;
                // 取创建时间与修改时间中较早的一个，尽量还原"最早"的语义
                return creation <= write ? creation : write;
            }
            catch (Exception)
            {
                return DateTime.MaxValue; // 无法读取时间的文件视为最新，最后删除
            }
        }
    }
}
