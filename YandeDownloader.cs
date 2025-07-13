// YandeDownloader.cs

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

public class YandeDownloader
{
    private const int MaxConcurrentDownloads = 8;

    private static readonly HttpClient httpClient = new();
    private static readonly object _logLock = new();
    private readonly string _errorFile = "下载错误.txt";
    private readonly string _logFile = "Ydown.log";
    private readonly string _manifestFile;
    private readonly string _outputDir;
    private readonly string _searchTags;

    private readonly string _sessionFile = "session.json";

    public YandeDownloader(string tags, string outputDir)
    {
        _searchTags = tags;
        _outputDir = outputDir;
        _manifestFile = Path.Combine(_outputDir, "manifest.json");
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task StartAsync(bool isResumed)
    {
        Directory.CreateDirectory(_outputDir);
        File.WriteAllText(_logFile, "");
        if (!File.Exists(_errorFile)) File.WriteAllText(_errorFile, "下载失败项目" + Environment.NewLine, Encoding.UTF8);

        var session = new SessionState { Tags = _searchTags, OutputDir = _outputDir };
        await File.WriteAllTextAsync(_sessionFile, JsonSerializer.Serialize(session));

        await LogAsync($"任务开始... 标签: '{_searchTags}', 目录: '{_outputDir}'");

        var manifest = await LoadManifestAsync();

        try
        {
            // 收集和筛选
            var allPosts = await FetchAllPostsMetadataAsync();
            var postsToDownload = FilterPosts(allPosts, manifest);
            if (isResumed && postsToDownload.Count > 0)
            {
                Console.WriteLine($"恢复任务: 经与服务器同步，共发现 {postsToDownload.Count} 个需要下载的文件 (包含上次未完成及新增文件)。");
                Console.Write("是否开始下载? (Y/n): ");
                var finalConfirm = Console.ReadLine()?.ToLower() ?? "y";
                if (finalConfirm != "y" && finalConfirm != "")
                {
                    await LogAsync("用户在最终确认时取消操作。");
                    // 保留 session 文件，以便下次还能恢复
                    return;
                }
            }

            if (postsToDownload.Count == 0)
            {
                await LogAsync("所有文件都已是最新版本，无需下载。");
                File.Delete(_sessionFile); // 任务完成，删除会话
                return;
            }

            await DownloadWithMultiSlotProgressAsync(postsToDownload, manifest);

            // 任务成功完成，删除会话文件
            File.Delete(_sessionFile);
        }
        catch (Exception ex)
        {
            await LogAsync($"发生严重错误: {ex.Message}");
            await LogAsync("任务中断。请检查日志。下次启动可选择恢复任务。");
        }
    }

    private async Task<ConcurrentDictionary<int, ManifestEntry>> LoadManifestAsync()
    {
        if (!File.Exists(_manifestFile)) return new ConcurrentDictionary<int, ManifestEntry>();

        try
        {
            var json = await File.ReadAllTextAsync(_manifestFile);
            var dictionary = JsonSerializer.Deserialize<Dictionary<int, ManifestEntry>>(json);
            return new ConcurrentDictionary<int, ManifestEntry>(dictionary ?? new Dictionary<int, ManifestEntry>());
        }
        catch (Exception ex)
        {
            await LogAsync($"警告: 加载清单文件 '{_manifestFile}' 失败: {ex.Message}. 将创建新清单。");
            return new ConcurrentDictionary<int, ManifestEntry>();
        }
    }

    private async Task SaveManifestAsync(ConcurrentDictionary<int, ManifestEntry> manifest)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(manifest, options);
        await File.WriteAllTextAsync(_manifestFile, json);
    }

    private async Task<List<Post>> FetchAllPostsMetadataAsync()
    {
        var allPosts = new List<Post>();
        var page = 1;
        Console.WriteLine("正在从服务器获取所有图片信息...");
        while (true)
        {
            var url = $"https://yande.re/post.json?limit=100&page={page}&tags={Uri.EscapeDataString(_searchTags)}";
            try
            {
                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var jsonStream = await response.Content.ReadAsStreamAsync();
                var posts = await JsonSerializer.DeserializeAsync<List<Post>>(jsonStream);

                if (posts == null || posts.Count == 0) break;

                allPosts.AddRange(posts);
                Console.Write($"\r已获取 {allPosts.Count} 个项目信息...");
                page++;
            }
            catch (Exception ex)
            {
                await LogAsync($"获取第 {page} 页数据时出错: {ex.Message}。已停止获取。");
                break;
            }
        }

        Console.WriteLine($"\n信息获取完成，共 {allPosts.Count} 个项目。");
        return allPosts;
    }

    private List<Post> FilterPosts(List<Post> allPosts, ConcurrentDictionary<int, ManifestEntry> manifest)
    {
        var postsToDownload = new List<Post>();
        foreach (var post in allPosts)
            if (manifest.TryGetValue(post.Id, out var existingEntry))
            {
                // ID 存在，检查文件大小
                if (existingEntry.FileSize != post.FileSize)
                {
                    postsToDownload.Add(post);
                    LogAsync($"ID: {post.Id} 大小不一致 (清单: {existingEntry.FileSize}, API: {post.FileSize})，将重新下载。").Wait();
                }
                // 如果大小一致，我们假设文件是好的，跳过
            }
            else
            {
                // ID 不存在于清单中，是新文件
                postsToDownload.Add(post);
            }

        return postsToDownload;
    }

    private async Task DownloadWithMultiSlotProgressAsync(List<Post> postsToDownload,
        ConcurrentDictionary<int, ManifestEntry> manifest)
    {
        using var workQueue = new BlockingCollection<Post>();

        foreach (var post in postsToDownload) workQueue.Add(post);
        workQueue.CompleteAdding();

        var ui = new MultiSlotConsoleUi(postsToDownload.Count, MaxConcurrentDownloads);
        ui.Initialize();

        var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        var periodicSaveTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    if (workQueue.IsCompleted && workQueue.Count == 0) continue;
                    await SaveManifestAsync(manifest);
                }
            }
            catch (TaskCanceledException)
            {
                await LogAsync("定时保存任务已收到停止信号，即将退出。");
            }
            catch (Exception ex)
            {
                await LogAsync($"定时保存任务发生错误: {ex.Message}");
            }
        }, cancellationToken);

        var consumerTasks = new List<Task>();
        for (var i = 0; i < MaxConcurrentDownloads; i++)
        {
            var slotNumber = i;
            consumerTasks.Add(Task.Run(async () =>
            {
                foreach (var post in workQueue.GetConsumingEnumerable())
                {
                    var apiFileSize = post.FileSize;
                    var totalSizeStr = apiFileSize > 0 ? FormatBytes(apiFileSize) : "???";
                    ui.SetSlotStatus(slotNumber, $"准备下载 ID: {post.Id}");

                    var progress = new Progress<long>(bytesDownloaded =>
                    {
                        var progressStr = FormatBytes(bytesDownloaded);
                        ui.SetSlotStatus(slotNumber, $"ID {post.Id}: {progressStr} / {totalSizeStr}");

                        var percentage = apiFileSize > 0 ? (double)bytesDownloaded / apiFileSize : 0;
                        ui.UpdateSlotProgress(slotNumber, percentage);
                    });

                    var actualDownloadedSize = await DownloadImageAsync(post, progress);

                    if (actualDownloadedSize > 0)
                    {
                        var entry = new ManifestEntry
                        {
                            FileSize = actualDownloadedSize,
                            FileName = $"{post.Id}.{post.FileExt}",
                            SearchTags = _searchTags,
                            DownloadedAt = DateTime.Now.ToLocalTime()
                        };
                        manifest.AddOrUpdate(post.Id, entry, (key, old) => entry);

                        ui.SetSlotStatus(slotNumber, $"完成 ID: {post.Id} ({FormatBytes(actualDownloadedSize)})", true);
                    }
                    else
                    {
                        ui.SetSlotStatus(slotNumber, $"错误 ID: {post.Id}", isError: true);
                    }

                    ui.IncrementTotalProgress();
                }

                ui.SetSlotStatus(slotNumber, "空闲");
            }));
        }

        await Task.WhenAll(consumerTasks);

        await LogAsync("所有下载任务已完成，正在停止定时保存服务...");
        cts.Cancel();
        await periodicSaveTask;

        ui.Finish();

        await LogAsync("正在保存最终的下载清单...");
        await SaveManifestAsync(manifest);
    }

    private async Task<long> DownloadImageAsync(Post post, IProgress<long> progress)
    {
        if (string.IsNullOrEmpty(post.FileUrl) || string.IsNullOrEmpty(post.FileExt)) return 0;

        var filePath = Path.Combine(_outputDir, $"{post.Id}.{post.FileExt}");

        try
        {
            var response = await httpClient.GetAsync(post.FileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead);
                totalBytesRead += bytesRead;
                progress.Report(totalBytesRead);
            }

            return totalBytesRead;
        }
        catch (Exception e)
        {
            await LogAsync($"下载 ID: {post.Id} 失败: {e.Message}");
            SaveError(post.Id);
            return 0;
        }
    }

    private void SaveError(int postId)
    {
        var errorUrl = $"https://yande.re/post/show/{postId}{Environment.NewLine}";
        lock (_logLock)
        {
            File.AppendAllText(_errorFile, errorUrl, Encoding.UTF8);
        }
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 0) return "N/A";
        if (bytes == 0) return "0 B";

        const int scale = 1024;
        string[] orders = { "B", "KB", "MB", "GB", "TB" };
        var i = (int)Math.Floor(Math.Log(bytes, scale));
        i = Math.Min(i, orders.Length - 1);
        var adjustedSize = bytes / Math.Pow(scale, i);

        return $"{adjustedSize:0.##} {orders[i]}";
    }

    private Task LogAsync(string message)
    {
        var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
        lock (_logLock)
        {
            File.AppendAllText(_logFile, logMessage + Environment.NewLine, Encoding.UTF8);
        }

        return Task.CompletedTask;
    }
}