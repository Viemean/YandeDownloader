// Program.cs

using System.Text;
using System.Text.Json;

internal class Program
{
    private const string SessionFile = "session.json";

    private static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var tags = "";
        var outputDir = "";
        var isResumed = false; // 标记是否为恢复的任务

        // 检查是否存在未完成的会话
        if (File.Exists(SessionFile))
        {
            var sessionJson = await File.ReadAllTextAsync(SessionFile);
            var lastSession = JsonSerializer.Deserialize<SessionState>(sessionJson);

            if (lastSession != null)
            {
                Console.WriteLine("检测到上一次有未完成的下载任务:");
                Console.WriteLine($"  - 标签: {lastSession.Tags}");
                Console.WriteLine($"  - 目录: {lastSession.OutputDir}");
                Console.Write("是否继续? (Y/n): ");
                var resume = Console.ReadLine()?.ToLower() ?? "y";
                if (resume == "y" || resume == "")
                {
                    tags = lastSession.Tags;
                    outputDir = lastSession.OutputDir;
                    isResumed = true; // 这是一个恢复的任务
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
            Console.WriteLine("================ Yande.re 下载器 (专业版) ================");
            // 1. 获取用户输入的标签
            Console.Write("请输入标签 (使用空格分隔, 使用 '-' 排除): ");
            tags = Console.ReadLine() ?? "";

// 2. 询问是否过滤 NSFW 内容
            Console.Write("选择过滤级别 (Explicit/Questionable/Safe)？(e/q/s): ");
            var nsfwInput = Console.ReadLine()?.ToLower() ?? ""; // 读取用户输入并转换为小写，如果为空则默认为 "e"
            switch (nsfwInput)
            {
                case "q": // 用户选择过滤 Questionable 内容
                    tags += " rating:questionable"; // 添加排除 questionable 和 explicit 评级的标签
                    Console.WriteLine("已添加 '-rating:q' 过滤规则。");
                    break;
                case "s": // 用户选择过滤 Safe 内容
                    tags += " rating:safe"; // 添加排除 safe, questionable 和 explicit 评级的标签
                    Console.WriteLine("已添加 '-rating:s' 过滤规则。");
                    break;
                case "e":
                    tags += " rating:explicit";
                    Console.WriteLine("已添加 '-rating:explicit' 过滤规则。");
                    break;
                default: // 任何其他无效输入也视为选择 Explicit
                    Console.WriteLine("无额外过滤规则。");
                    break;
            }

            tags = tags.Trim(); // 去除首尾可能多余的空格

// 在这里，您可以继续使用完善后的 tags 字符串进行后续操作
            Console.WriteLine($"最终的标签字符串为: \"{tags}\"");

            // 3. 获取输出目录
            var defaultOutputDir = Path.Combine(Directory.GetCurrentDirectory(), "Download");
            Console.WriteLine($"请输入输出目录 (直接回车将使用默认路径: {defaultOutputDir}):");
            outputDir = Console.ReadLine() ?? "";
            if (string.IsNullOrWhiteSpace(outputDir)) outputDir = defaultOutputDir;
        }

        Console.WriteLine("---------------------------------------------------------");

        // 创建下载器实例并启动
        var downloader = new YandeDownloader(tags, outputDir);
        await downloader.StartAsync(isResumed); // 传入恢复状态

        Console.WriteLine("\n======================= 操作完成 ========================");
        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
}