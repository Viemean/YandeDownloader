using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace YandeDownloader;

public class Downloader
{
    private const int MaxConcurrentDownloads = 8;
    private const string ErrorFile = "下载错误.txt";
    private const string LogFile = "Ydown.log";
    private const string SessionFile = "session.json";

    private static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = MaxConcurrentDownloads * 2 // 突破默认连接限制
    })
    {
        DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)" } }
    };

    private static readonly Lock LogLock = new();
    private readonly string _manifestFile;
    private readonly string _outputDir;
    private readonly string _searchTags;

    //仅在有新文件下载完成时才触发磁盘写入
    private volatile bool _isManifestDirty;

    public Downloader(string searchTags, string outputDir)
    {
        _searchTags = searchTags;
        _outputDir = outputDir;
        _manifestFile = Path.Combine(_outputDir, "manifest.json");
    }

    public async Task StartAsync(bool isResumed)
    {
        Directory.CreateDirectory(_outputDir);
        await File.WriteAllTextAsync(LogFile, "");
        if (!File.Exists(ErrorFile))
            await File.WriteAllTextAsync(ErrorFile, "下载失败项目" + Environment.NewLine, Encoding.UTF8);

        var session = new SessionState { SearchTags = _searchTags, OutputDir = _outputDir };
        var jsonString = JsonSerializer.Serialize(session, SourceGenerationContext.Default.SessionState);
        await File.WriteAllTextAsync(SessionFile, jsonString);
        await LogAsync($"任务开始... 标签: '{_searchTags}', 目录: '{_outputDir}'");

        var manifest = await LoadManifestAsync();

        try
        {
            var allPosts = await FetchAllPostsMetadataAsync();
            var postsToDownload = FilterPosts(allPosts, manifest);

            if (isResumed && postsToDownload.Count > 0)
            {
                Console.WriteLine($"恢复任务: 经与服务器同步，共发现 {postsToDownload.Count} 个需要下载的文件。");
                Console.Write("是否开始下载? (Y/n): ");
                var finalConfirm = Console.ReadLine()?.ToLower() ?? "y";
                if (finalConfirm != "y" && finalConfirm != "")
                {
                    await LogAsync("用户在最终确认时取消操作。");
                    return;
                }
            }

            if (postsToDownload.Count == 0)
            {
                await LogAsync("所有文件都已是最新版本，无需下载。");
                File.Delete(SessionFile);
                return;
            }

            await DownloadWithMultiSlotProgressAsync(postsToDownload, manifest);
            File.Delete(SessionFile);
        }
        catch (Exception ex)
        {
            await LogAsync($"发生严重错误: {ex.Message}");
            await LogAsync("任务中断。请检查日志。下次启动可选择恢复任务。");
        }
    }

    private async Task<ConcurrentDictionary<int, ManifestEntry>> LoadManifestAsync()
    {
        if (!File.Exists(_manifestFile))
            return new ConcurrentDictionary<int, ManifestEntry>();

        try
        {
            await using var stream = File.OpenRead(_manifestFile);
            var dictionary = await JsonSerializer.DeserializeAsync<Dictionary<int, ManifestEntry>>(
                stream, SourceGenerationContext.Default.DictionaryInt32ManifestEntry);

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
        await using var stream = new FileStream(_manifestFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, manifest,
            SourceGenerationContext.Default.ConcurrentDictionaryInt32ManifestEntry);
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
                var response = await HttpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();
                var jsonStream = await response.Content.ReadAsStreamAsync();
                var posts = await JsonSerializer.DeserializeAsync<List<Post>>(jsonStream,
                    SourceGenerationContext.Default.ListPost);

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

    private static List<Post> FilterPosts(List<Post> allPosts, ConcurrentDictionary<int, ManifestEntry> manifest)
    {
        var postsToDownload = new List<Post>();
        foreach (var post in allPosts)
            if (manifest.TryGetValue(post.Id, out var existingEntry))
            {
                if (existingEntry.FileSize == post.FileSize) continue;
                postsToDownload.Add(post);
                LogAsync($"ID: {post.Id} 大小不一致 (清单: {existingEntry.FileSize}, 实际: {post.FileSize})，将重新下载。").Wait();
            }
            else
            {
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

        // 这里 UI 已经通过构造函数知道了 MaxConcurrentDownloads，也就是 8
        var ui = new MultiSlotConsoleUi(postsToDownload.Count, MaxConcurrentDownloads);
        ui.Initialize(); // 这步会锁定并画出 8 行“空闲”通道

        var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        var periodicSaveTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    if (_isManifestDirty)
                    {
                        _isManifestDirty = false;
                        await SaveManifestAsync(manifest);
                    }
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                await LogAsync($"定时保存任务发生错误: {ex.Message}");
            }
        }, cancellationToken);

        var consumerTasks = new List<Task>();
        var consumingEnumerable = workQueue.GetConsumingEnumerable();

        for (var i = 0; i < MaxConcurrentDownloads; i++)
        {
            var slotNumber = i;
            consumerTasks.Add(Task.Run(async () =>
            {
                foreach (var post in consumingEnumerable)
                {
                    var apiFileSize = post.FileSize;

                    ui.SetSlotStatus(slotNumber, $"准备下载 ID: {post.Id}");

                    var progress = new Progress<long>(bytesDownloaded =>
                    {
                        if (apiFileSize <= 0) return;
                        var percentage = (double)bytesDownloaded / apiFileSize;
                        ui.UpdateSlotProgress(slotNumber, percentage);
                    });

                    var downloadedSize = await DownloadImageAsync(post, progress);

                    if (downloadedSize > 0)
                    {
                        var entry = new ManifestEntry
                        {
                            FileName = $"{post.Id}.{post.FileExt}",
                            FileSize = downloadedSize,
                            Tags = post.Tags,
                            SearchTags = _searchTags,
                            FileUrl = post.FileUrl
                        };
                        manifest.AddOrUpdate(post.Id, entry, (_, _) => entry);
                        _isManifestDirty = true;

                        ui.SetSlotStatus(slotNumber, $"完成 ID: {post.Id} ({FormatBytes(downloadedSize)})",
                            true);
                    }
                    else
                    {
                        ui.SetSlotStatus(slotNumber, $"错误 ID: {post.Id}", isError: true);
                    }

                    ui.IncrementTotalProgress();
                }

                ui.SetSlotStatus(slotNumber, "空闲");
            }, cancellationToken));
        }

        await Task.WhenAll(consumerTasks);

        await LogAsync("所有下载任务已完成，正在停止定时保存服务...");
        await cts.CancelAsync();
        await periodicSaveTask;

        ui.Finish(); // 将游标移出 UI 区域

        await LogAsync("正在保存最终的下载清单...");
        await SaveManifestAsync(manifest);
    }

    private async Task<long> DownloadImageAsync(Post post, IProgress<long> progress)
    {
        if (string.IsNullOrEmpty(post.FileUrl) || string.IsNullOrEmpty(post.FileExt)) return 0;
        var filePath = Path.Combine(_outputDir, $"{post.Id}.{post.FileExt}");

        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            var response = await HttpClient.GetAsync(post.FileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous);

            long totalBytesRead = 0;
            int bytesRead;

            // 使用 Memory 进行读写
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory())) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalBytesRead += bytesRead;
                progress.Report(totalBytesRead);
            }

            return post.FileSize;
        }
        catch (Exception e)
        {
            await LogAsync($"下载 ID: {post.Id} 失败: {e.Message}");
            SaveError(post.Id, post.FileUrl);
            return 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SaveError(int postId, string errorFileUrl)
    {
        var errorUrl = $"https://yande.re/post/show/{postId}{Environment.NewLine}";
        lock (LogLock)
        {
            File.AppendAllText(ErrorFile, errorUrl, Encoding.UTF8);
            File.AppendAllText(ErrorFile, errorFileUrl + Environment.NewLine, Encoding.UTF8);
        }
    }

    private static string FormatBytes(long bytes)
    {
        switch (bytes)
        {
            case < 0: return "N/A";
            case 0: return "0 B";
        }

        const int scale = 1024;
        string[] orders = ["B", "KB", "MB", "GB", "TB"];
        var i = (int)Math.Floor(Math.Log(bytes, scale));
        i = Math.Min(i, orders.Length - 1);
        var adjustedSize = bytes / Math.Pow(scale, i);
        return $"{adjustedSize:0.##} {orders[i]}";
    }

    private static Task LogAsync(string message)
    {
        var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}{Environment.NewLine}";
        lock (LogLock)
        {
            File.AppendAllText(LogFile, logMessage, Encoding.UTF8);
        }

        return Task.CompletedTask;
    }
}