namespace YandeDownloader;

public class MultiSlotConsoleUi
{
    private readonly int _totalItems;
    private readonly int _numSlots;

    private int _totalProgress;

    private readonly Lock _consoleLock = new();

    // 记录 UI 在控制台中的起始行号
    private int _uiTopRow;

    // 缓存每个 Slot 的当前状态文本和百分比，用于重绘
    private readonly string[] _slotStatuses;
    private readonly double[] _slotProgress;

    public MultiSlotConsoleUi(int totalItems, int numSlots)
    {
        _totalItems = totalItems;
        _numSlots = numSlots;
        _slotStatuses = new string[numSlots];
        _slotProgress = new double[numSlots];

        for (var i = 0; i < numSlots; i++) _slotStatuses[i] = "空闲";
    }

    public void Initialize()
    {
        lock (_consoleLock)
        {
            Console.WriteLine(new string('=', Console.WindowWidth - 1));

            _uiTopRow = Console.CursorTop;

            Console.WriteLine("总进度: Waiting...");

            for (var i = 0; i < _numSlots; i++) Console.WriteLine($"[通道 {i + 1}] 空闲");

            Console.WriteLine(new string('-', Console.WindowWidth - 1));

            UpdateTotalDisplay();
            for (var i = 0; i < _numSlots; i++) RedrawSlot(i);
        }
    }

    private void SafeDrawLine(int relativeLineIndex, string text, ConsoleColor? color = null)
    {
        lock (_consoleLock)
        {
            var originalLeft = Console.CursorLeft;
            var originalTop = Console.CursorTop;
            var originalColor = Console.ForegroundColor;

            try
            {
                var targetTop = _uiTopRow + relativeLineIndex;

                if (targetTop >= Console.BufferHeight) return;
                Console.SetCursorPosition(0, targetTop);

                Console.Write(new string(' ', Console.WindowWidth - 1));
                Console.SetCursorPosition(0, targetTop);

                if (color.HasValue) Console.ForegroundColor = color.Value;
                if (text.Length >= Console.WindowWidth) text = text[..(Console.WindowWidth - 2)];
                Console.Write(text);
            }
            catch
            {
                /* 忽略绘图错误 */
            }
            finally
            {
                Console.ForegroundColor = originalColor;
                Console.SetCursorPosition(originalLeft, originalTop);
            }
        }
    }

    private static string GetProgressBar(double percent)
    {
        const int blockCount = 20;
        var filled = (int)(percent * blockCount);
        if (filled < 0) filled = 0;
        if (filled > blockCount) filled = blockCount;
        return $"[{new string('#', filled)}{new string('-', blockCount - filled)}] {percent * 100:0}%";
    }

    public void IncrementTotalProgress()
    {
        Interlocked.Increment(ref _totalProgress);
        UpdateTotalDisplay();
    }

    private void UpdateTotalDisplay()
    {
        var progress = _totalProgress;
        var percent = _totalItems == 0 ? 0 : (double)progress / _totalItems;
        var bar = GetProgressBar(percent);
        var text = $"总进度: {progress}/{_totalItems} {bar}";

        SafeDrawLine(0, text, ConsoleColor.Cyan);
    }

    public void UpdateSlotProgress(int slotNumber, double percentage)
    {
        _slotProgress[slotNumber] = percentage;
        RedrawSlot(slotNumber);
    }

    public void SetSlotStatus(int slotNumber, string status, bool isFinished = false, bool isError = false)
    {
        _slotStatuses[slotNumber] = status;

        if (isFinished || isError) _slotProgress[slotNumber] = 0;

        RedrawSlot(slotNumber, isFinished, isError);
    }

    private void RedrawSlot(int slotNumber, bool isFinished = false, bool isError = false)
    {
        var status = _slotStatuses[slotNumber];

        var bar = status == "空闲" || isFinished || isError ? "" : GetProgressBar(_slotProgress[slotNumber]);

        var text = $"[通道 {slotNumber + 1}] {status} {bar}";

        ConsoleColor? color = null;
        if (isFinished) color = ConsoleColor.Green;
        else if (isError) color = ConsoleColor.Red;
        else if (status == "空闲") color = ConsoleColor.Gray;

        SafeDrawLine(slotNumber + 1, text, color);
    }

    public void Finish()
    {
        lock (_consoleLock)
        {
            var bottomLine = _uiTopRow + _numSlots + 2;
            if (bottomLine < Console.BufferHeight)
                Console.SetCursorPosition(0, bottomLine);
            else
                Console.SetCursorPosition(0, Console.BufferHeight - 1);
        }
    }
}