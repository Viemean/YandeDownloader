/// <summary>
///     多线程下载进度条
/// </summary>
/// <param name="totalFiles">总下载文件数量</param>
/// <param name="slotCount">进度条数量</param>
public class MultiSlotConsoleUi(int totalFiles, int slotCount)
{
    private readonly Lock _lock = new();
    private readonly double[] _slotProgress = new double[slotCount];
    private readonly string[] _slotStatus = new string[slotCount];
    private int _completedFiles;
    private int _uiStartPosition;

    /// <summary>
    ///     初始化下载进度条
    /// </summary>
    public void Initialize()
    {
        lock (_lock)
        {
            Console.CursorVisible = false;
            Console.WriteLine("下载即将开始...");
            Console.WriteLine(new string('=', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 50));
            _uiStartPosition = Console.CursorTop;
            for (var i = 0; i < slotCount + 1; i++) Console.WriteLine();
            Console.WriteLine(new string('=', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 50));
            Draw();
        }
    }

    private void Draw()
    {
        Console.SetCursorPosition(0, _uiStartPosition);

        // 绘制总进度
        DrawProgressBar($"总进度 ({_completedFiles}/{totalFiles})", (double)_completedFiles / totalFiles,
            Console.WindowWidth - 2);
        Console.WriteLine();

        // 绘制每个槽位
        for (var i = 0; i < slotCount; i++)
        {
            var status = _slotStatus[i];
            var progress = _slotProgress[i];
            DrawProgressBar($"槽 {i + 1}: {status,-30}", progress, Console.WindowWidth - 2);
            Console.WriteLine();
        }
    }

    public void SetSlotStatus(int slot, string status, bool isSuccess = false, bool isError = false)
    {
        lock (_lock)
        {
            var originalColor = Console.ForegroundColor;
            if (isSuccess) Console.ForegroundColor = ConsoleColor.Green;
            if (isError) Console.ForegroundColor = ConsoleColor.Red;

            _slotStatus[slot] = status;
            Draw();

            Console.ForegroundColor = originalColor;
        }
    }

    public void UpdateSlotProgress(int slot, double progress)
    {
        lock (_lock)
        {
            _slotProgress[slot] = progress;
            Draw();
        }
    }

    public void IncrementTotalProgress()
    {
        lock (_lock)
        {
            _completedFiles++;
            Draw();
        }
    }

    /// <summary>
    ///     绘制进度条
    /// </summary>
    /// <param name="label">进度条标签</param>
    /// <param name="percentage">百分比</param>
    /// <param name="totalWidth">总宽度</param>
    private void DrawProgressBar(string label, double percentage, int totalWidth)
    {
        var labelWidth = label.Length;
        var barWidth = totalWidth - labelWidth - 5;
        if (barWidth < 10) barWidth = 10;

        var progress = (int)(percentage * barWidth);

        var bar = $"[{new string('█', progress)}{new string('─', barWidth - progress)}]";
        var line = $"{label} {bar} {percentage:P0} ".PadRight(totalWidth);
        Console.Write("\r" + line[..Math.Min(line.Length, totalWidth)]);
    }

    public void Finish()
    {
        lock (_lock)
        {
            Console.SetCursorPosition(0, _uiStartPosition + slotCount + 3);
            Console.WriteLine("所有下载任务处理完毕。");
            Console.CursorVisible = true;
        }
    }
}