using System.Text;
using System.Text.Json;

namespace YandeDownloader;
internal static class Program
{
    private const string SessionFile = "session.json";

    private static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var searchTags = "";
        var outputDir = "";
        //是否继续下载
        var isResumed = false;

        // 检查是否存在未完成的会话
        if (File.Exists(SessionFile))
        {
            var sessionJson = await File.ReadAllTextAsync(SessionFile);
            var lastSession =
                JsonSerializer.Deserialize<SessionState>(sessionJson, SourceGenerationContext.Default.SessionState);

            if (lastSession != null)
            {
                Console.WriteLine("检测到上一次有未完成的下载任务:");
                Console.WriteLine($"  - 标签: {lastSession.SearchTags}");
                Console.WriteLine($"  - 目录: {lastSession.OutputDir}");
                Console.Write("是否继续? (Y/n): ");
                var resume = Console.ReadLine()?.ToLower() ?? "y";
                if (resume == "y" || resume == "")
                {
                    searchTags = lastSession.SearchTags;
                    outputDir = lastSession.OutputDir;
                    isResumed = true;
                    Console.WriteLine("已选择恢复任务，将开始同步服务器最新文件列表...");
                }
                else
                {
                    // 用户选择放弃，删除会话文件
                    File.Delete(SessionFile);
                    Console.WriteLine("已放弃上一次的任务。");
                }
            }
        }

        // 如果不是恢复任务，则走标准输入流程
        if (!isResumed)
        {
            Console.WriteLine("================ Yande.re下载器 ================");
            // 1. 获取用户输入的标签
            Console.Write("请输入标签 (使用空格分隔, 使用 '-' 排除指定标签)");
            //预设值为空
            searchTags = Console.ReadLine() ?? "";

            // 2. 询问是否过滤级别
            Console.Write("选择过滤级别 (Explicit/Questionable/Safe)？(e/q/s): ");
            var nsfwInput = Console.ReadLine()?.ToLower() ?? "";
            switch (nsfwInput)
            {
                case "q":
                    searchTags += " rating:questionable";
                    Console.WriteLine("已添加 '-rating:q' 过滤规则。");
                    break;
                case "s":
                    searchTags += " rating:safe";
                    Console.WriteLine("已添加 '-rating:s' 过滤规则。");
                    break;
                case "e":
                    searchTags += " rating:explicit";
                    Console.WriteLine("已添加 '-rating:explicit' 过滤规则。");
                    break;
                default:
                    Console.WriteLine("无额外过滤规则。");
                    break;
            }

            searchTags = searchTags.Trim();

            Console.WriteLine($"最终的标签字符串为: \"{searchTags}\"");

            // 3. 获取输出目录
            var defaultOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "Download");
            Console.WriteLine($"请输入输出目录 (直接回车将使用默认路径: {defaultOutputDir}):");
            outputDir = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = defaultOutputDir;
        }

        Console.WriteLine("---------------------------------------------------------");

        // 创建下载器实例并启动
        var downloader = new Downloader(searchTags, outputDir);
        await downloader.StartAsync(isResumed);

        Console.WriteLine("\n======================= 操作完成 ========================");
        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
}