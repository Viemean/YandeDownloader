//多线程进度条

public class MultiSlotConsoleUi
{
    private readonly object _lock = new();
    private readonly int _slotCount;
    private readonly double[] _slotProgress;
    private readonly string[] _slotStatus;
    private readonly int _totalFiles;
    private int _completedFiles;
    private int _uiStartPosition; // 将 readonly 移除

    public MultiSlotConsoleUi(int totalFiles, int slotCount)
    {
        _totalFiles = totalFiles;
        _slotCount = slotCount;
        _slotStatus = new string[slotCount];
        _slotProgress = new double[slotCount];
    }

    public void Initialize()
    {
        lock (_lock)
        {
            Console.CursorVisible = false;
            Console.WriteLine("下载即将开始...");
            Console.WriteLine(new string('=', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80));
            _uiStartPosition = Console.CursorTop;
            for (var i = 0; i < _slotCount + 2; i++) Console.WriteLine();
            Console.WriteLine(new string('=', Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 80));
            Draw();
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

    private void Draw()
    {
        Console.SetCursorPosition(0, _uiStartPosition);

        // 绘制总进度
        DrawProgressBar($"总进度 ({_completedFiles}/{_totalFiles})", (double)_completedFiles / _totalFiles,
            Console.WindowWidth - 2);
        Console.WriteLine();

        // 绘制每个槽位
        for (var i = 0; i < _slotCount; i++)
        {
            var status = _slotStatus[i] ?? "初始化...";
            var progress = _slotProgress[i];
            DrawProgressBar($"槽 {i + 1}: {status.PadRight(30)}", progress, Console.WindowWidth - 2);
            Console.WriteLine();
        }
    }

    private void DrawProgressBar(string label, double percentage, int totalWidth)
    {
        var labelWidth = label.Length;
        var barWidth = totalWidth - labelWidth - 5;
        if (barWidth < 10) barWidth = 10;

        var progress = (int)(percentage * barWidth);

        var bar = $"[{new string('█', progress)}{new string('─', barWidth - progress)}]";
        var line = $"{label} {bar} {percentage:P0} ".PadRight(totalWidth);
        Console.Write("\r" + line.Substring(0, Math.Min(line.Length, totalWidth)));
    }

    public void Finish()
    {
        lock (_lock)
        {
            Console.SetCursorPosition(0, _uiStartPosition + _slotCount + 3);
            Console.WriteLine("所有下载任务处理完毕。");
            Console.CursorVisible = true;
        }
    }
}