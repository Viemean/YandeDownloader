// YandeDownloader.cs

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

public class YandeDownloader
{
    //最大线程
    private const int MaxConcurrentDownloads = 5;
    private const string ErrorFile = "下载错误.txt";
    private const string LogFile = "Ydown.log";
    private const string SessionFile = "session.json";
    private static readonly HttpClient HttpClient = new();
    private static readonly Lock LogLock = new();
    private readonly string _manifestFile;
    private readonly string _outputDir;
    private readonly string _searchTags;

    public YandeDownloader(string searchTags, string outputDir)
    {
        _searchTags = searchTags;
        _outputDir = outputDir;
        _manifestFile = Path.Combine(_outputDir, "manifest.json");
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public async Task StartAsync(bool isResumed)
    {
        //新建下载目录
        Directory.CreateDirectory(_outputDir);
        //创建log文件
        await File.WriteAllTextAsync(LogFile, "");
        //创建错误文件
        if (!File.Exists(ErrorFile))
            await File.WriteAllTextAsync(ErrorFile, "下载失败项目" + Environment.NewLine, Encoding.UTF8);

        //创建存储会话实例
        var session = new SessionState { SearchTags = _searchTags, OutputDir = _outputDir };
        //使用自定义源生成器
        var jsonString = JsonSerializer.Serialize(session, SourceGenerationContext.Default.SessionState);
        //将当前搜索tags和目录写入到Session文件
        await File.WriteAllTextAsync(SessionFile, jsonString);
        //写入日志
        await LogAsync($"任务开始... 标签: '{_searchTags}', 目录: '{_outputDir}'");
        //读取manifest文件内已下载文件数据
        var manifest = await LoadManifestAsync();

        try
        {
            // 抓取json数据
            var allPosts = await FetchAllPostsMetadataAsync();
            //检查文件是否存在
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
                File.Delete(SessionFile); // 任务完成，删除会话
                return;
            }

            //启动多线程下载
            await DownloadWithMultiSlotProgressAsync(postsToDownload, manifest);
            // 任务成功完成，删除会话文件
            File.Delete(SessionFile);
        }
        catch (Exception ex)
        {
            await LogAsync($"发生严重错误: {ex.Message}");
            await LogAsync("任务中断。请检查日志。下次启动可选择恢复任务。");
        }
    }

    /// <summary>
    ///     加载本地manifest文件数据
    /// </summary>
    /// <returns>如果文件存在且解析成功则返回其中数据，否者返回一个空的ManifestEntry</returns>
    private async Task<ConcurrentDictionary<int, ManifestEntry>> LoadManifestAsync()
    {
        //如果不存在则返回一个新的ManifestEntry
        if (!File.Exists(_manifestFile))
            return new ConcurrentDictionary<int, ManifestEntry>();
        //尝试解析Manifest文件数据
        try
        {
            var json = await File.ReadAllTextAsync(_manifestFile);
            var dictionary = JsonSerializer.Deserialize<Dictionary<int, ManifestEntry>>(json,
                SourceGenerationContext.Default.DictionaryInt32ManifestEntry);
            //创建线程安全的字典，并检查空值，如果为空则创建新的ManifestEntry
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
        var json = JsonSerializer.Serialize(manifest,
            SourceGenerationContext.Default.ConcurrentDictionaryInt32ManifestEntry);
        await File.WriteAllTextAsync(_manifestFile, json);
    }

    /// <summary>
    ///     抓取json数据并解析标签对应数据
    /// </summary>
    /// <returns>返回全部解析的数据</returns>
    private async Task<List<Post>> FetchAllPostsMetadataAsync()
    {
        var allPosts = new List<Post>();
        var page = 1;
        Console.WriteLine("正在从服务器获取所有图片信息...");
        while (true)
        {
            //转换搜索Tags避免空格和特殊符号造成错误
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
                //更新当前控制台文本
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

    /// <summary>
    ///     检查远程json数据是否已被下载记录到manifset中，检查id和大小是否一直
    /// </summary>
    /// <param name="allPosts">网络解析的json数据</param>
    /// <param name="manifest">用户已解析的本地json数据</param>
    /// <returns>返回可下载的文件数量</returns>
    private static List<Post> FilterPosts(List<Post> allPosts, ConcurrentDictionary<int, ManifestEntry> manifest)
    {
        var postsToDownload = new List<Post>();
        foreach (var post in allPosts)
            //先在manifest中寻找id是否存在，找到后返回true并赋值给existingEntry
            if (manifest.TryGetValue(post.Id, out var existingEntry))
            {
                // ID 存在，检查文件大小
                if (existingEntry.FileSize == post.FileSize) continue;
                postsToDownload.Add(post);
                LogAsync($"ID: {post.Id} 大小不一致 (清单: {existingEntry.FileSize}, 大小: {post.FileUrl})，将重新下载。").Wait();
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
        //创建新的工作队列
        using var workQueue = new BlockingCollection<Post>();

        //将要下载任务全部加入队列中
        foreach (var post in postsToDownload)
            workQueue.Add(post);
        workQueue.CompleteAdding();

        //创建控制台进度条
        var ui = new MultiSlotConsoleUi(postsToDownload.Count, MaxConcurrentDownloads);
        ui.Initialize();

        //创建取消token用于取消任务
        var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        var periodicSaveTask = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    //每隔两秒保存一次
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
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

        //创建多个并行下载任务
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
                    //如果大小大于0调用FormatBytes方法，否者赋值？？？
                    var totalSizeStr = apiFileSize > 0 ? FormatBytes(apiFileSize) : "???";
                    ui.SetSlotStatus(slotNumber, $"准备下载 ID: {post.Id}");

                    var progress = new Progress<long>(bytesDownloaded =>
                    {
                        var progressStr = FormatBytes(bytesDownloaded);
                        ui.SetSlotStatus(slotNumber, $"ID {post.Id}: {progressStr} / {totalSizeStr}");

                        var percentage = apiFileSize > 0 ? (double)bytesDownloaded / apiFileSize : 0;
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
                            SearchTags = _searchTags
                        };
                        manifest.AddOrUpdate(post.Id, entry, (key, old) => entry);

                        ui.SetSlotStatus(slotNumber, $"完成 ID: {post.Id} ({FormatBytes(downloadedSize)})", true);
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

        ui.Finish();

        await LogAsync("正在保存最终的下载清单...");
        await SaveManifestAsync(manifest);
    }

    /// <summary>
    /// </summary>
    /// <param name="post">下载的图片</param>
    /// <param name="progress"></param>
    /// <returns>下载文件的字节</returns>
    private async Task<long> DownloadImageAsync(Post post, IProgress<long> progress)
    {
        if (string.IsNullOrEmpty(post.FileUrl) || string.IsNullOrEmpty(post.FileExt)) return 0;

        var filePath = Path.Combine(_outputDir, $"{post.Id}.{post.FileExt}");

        try
        {
            var response = await HttpClient.GetAsync(post.FileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                //写入文件
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalBytesRead += bytesRead;
                progress.Report(totalBytesRead);
            }

            return post.FileSize;
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
        lock (LogLock)
        {
            File.AppendAllText(ErrorFile, errorUrl, Encoding.UTF8);
        }
    }

    private string FormatBytes(long bytes)
    {
        switch (bytes)
        {
            case < 0:
                return "N/A";
            case 0:
                return "0 B";
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
        var logMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
        lock (LogLock)
        {
            File.AppendAllText(LogFile, logMessage + Environment.NewLine, Encoding.UTF8);
        }

        return Task.CompletedTask;
    }
}