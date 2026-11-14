using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        /// TODO: 上游若新增 HLS/fMP4 等产出非 .flv 的录制模式（.m4s/.mp4），
        /// 此列表需要同步扩展，否则容量限制会静默失效。
        /// </summary>
        private static readonly string[] VideoFileExtensions = new[] { ".flv" };

        /// <summary>
        /// 相同文件夹两次检查之间的最小间隔。每次新建录制文件都会触发检查，
        /// 而检查是完整目录遍历 + 全量 stat——本地 NVMe 无感，但 NAS/机械盘上
        /// 会拖慢录制管线的建文件路径，且多房间高频触发完全没必要。
        /// </summary>
        private static readonly TimeSpan MinCheckInterval = TimeSpan.FromMinutes(5);

        /// <summary>
        /// 每个文件夹上次检查的时间（UTC ticks），用于节流。
        /// </summary>
        private static readonly ConcurrentDictionary<string, long> LastCheckTicks = new();

        /// <summary>
        /// 正在后台执行检查的文件夹，防止同一文件夹并发跑多个扫描。
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> RunningChecks = new();

        /// <summary>
        /// 检查文件夹总大小，超出限制时在后台线程删除最早的录制视频文件。
        /// 检查涉及完整目录遍历 + 全量 stat，在 NAS/机械盘上可能耗时数秒，
        /// 因此不在调用方（录制管线的建文件路径）上同步执行，立即返回。
        /// </summary>
        /// <param name="folder">要检查的文件夹</param>
        /// <param name="maxBytes">大小上限（字节）</param>
        /// <param name="logger">日志</param>
        /// <param name="searchOption">是否递归子目录。扁平模板下 folder 是整个
        /// 工作目录，调用方应传 TopDirectoryOnly 以免波及其他主播的文件</param>
        public static void EnforceLimit(string folder, long maxBytes, ILogger logger,
            SearchOption searchOption = SearchOption.AllDirectories)
        {
            if (string.IsNullOrEmpty(folder) || maxBytes <= 0 || !Directory.Exists(folder))
                return;

            // 节流：同文件夹在最小间隔内的重复检查直接跳过。多线程同时触发时
            // 偶尔多跑一次无害，不追求精确的 CAS 语义。
            var key = Path.GetFullPath(folder)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var now = DateTime.UtcNow.Ticks;
            if (LastCheckTicks.TryGetValue(key, out var last) && now - last < MinCheckInterval.Ticks)
                return;
            LastCheckTicks[key] = now;

            // 该文件夹已有检查在后台执行时跳过（顺带消耗掉本次节流窗口，无妨）
            if (!RunningChecks.TryAdd(key, 0))
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    ScanAndEnforce(folder, key, maxBytes, logger, searchOption);
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "检查录制文件夹 {Folder} 大小限制时出错", folder);
                }
                finally
                {
                    RunningChecks.TryRemove(key, out _);
                }
            });
        }

        /// <summary>
        /// 后台执行的实际检查逻辑：统计总大小，超出限制时按创建时间从早到晚删除录制视频文件。
        /// </summary>
        private static void ScanAndEnforce(string folder, string key, long maxBytes, ILogger logger,
            SearchOption searchOption)
        {
            var files = CollectFiles(folder, logger, searchOption);
            if (files.Count == 0)
                return;

            long totalSize = 0;
            foreach (var file in files)
            {
                try { totalSize += file.Length; }
                catch (Exception) { }
            }

            if (totalSize <= maxBytes)
                return;

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

            if (deletedCount > 0)
                logger.Information("为控制录制文件夹 {Folder} 大小，共删除了 {Count} 个旧录制文件", folder, deletedCount);
        }

        /// <summary>
        /// 手动按目录递归收集文件。不用 <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/>：
        /// 它的迭代器抛过一次异常就进入 faulted 终态，后续内容会被静默跳过，
        /// 导致大小统计少算。这里逐目录 catch，跳过出问题的部分并留下 Warning。
        /// </summary>
        private static List<FileInfo> CollectFiles(string folder, ILogger logger, SearchOption searchOption)
        {
            var result = new List<FileInfo>();
            var pendingDirs = new Stack<string>();
            pendingDirs.Push(folder);

            while (pendingDirs.Count > 0)
            {
                var dir = pendingDirs.Pop();

                try
                {
                    result.AddRange(new DirectoryInfo(dir).EnumerateFiles());
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "枚举录制文件夹 {Dir} 中的文件失败，该部分不计入大小统计", dir);
                }

                if (searchOption != SearchOption.AllDirectories)
                    continue;

                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        pendingDirs.Push(sub);
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "枚举录制文件夹 {Dir} 的子目录失败，子目录内容不计入大小统计", dir);
                }
            }

            return result;
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
