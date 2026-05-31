using System;

namespace SyncSession.Samples.Console.Infrastructure;

/// <summary>
/// Helper for colored console output and progress visualization
/// </summary>
public static class OutputHelper
{
    /// <summary>
    /// Write success message in green with ✓
    /// </summary>
    public static void WriteSuccess(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($"✓ {message}");
        System.Console.ResetColor();
    }

    /// <summary>
    /// Write info message in cyan with →
    /// </summary>
    public static void WriteInfo(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"→ {message}");
        System.Console.ResetColor();
    }

    /// <summary>
    /// Write warning message in yellow with ⚠
    /// </summary>
    public static void WriteWarning(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.WriteLine($"⚠ {message}");
        System.Console.ResetColor();
    }

    /// <summary>
    /// Write error message in red with ✗
    /// </summary>
    public static void WriteError(string message)
    {
        System.Console.ForegroundColor = ConsoleColor.Red;
        System.Console.WriteLine($"✗ {message}");
        System.Console.ResetColor();
    }

    /// <summary>
    /// Write header with banner box
    /// </summary>
    public static void WriteHeader(string title)
    {
        var length = title.Length + 4;
        var border = new string('=', length);
        
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.White;
        System.Console.WriteLine(border);
        System.Console.WriteLine($"  {title}");
        System.Console.WriteLine(border);
        System.Console.ResetColor();
        System.Console.WriteLine();
    }

    /// <summary>
    /// Get ASCII progress bar
    /// </summary>
    /// <param name="percent">Progress percentage (0-100)</param>
    /// <param name="width">Total width of progress bar</param>
    /// <returns>Progress bar string like [████████░░░░]</returns>
    public static string GetProgressBar(double percent, int width = 40)
    {
        var filled = (int)Math.Round(percent / 100.0 * width);
        var empty = width - filled;
        
        var filledBar = new string('█', filled);
        var emptyBar = new string('░', empty);
        
        return $"[{filledBar}{emptyBar}]";
    }

    /// <summary>
    /// Clear the current console line (for updating progress)
    /// </summary>
    public static void ClearLine()
    {
        System.Console.Write("\r" + new string(' ', System.Console.WindowWidth - 1) + "\r");
    }

    /// <summary>
    /// Write progress update on same line (use with ClearLine for updates)
    /// </summary>
    public static void WriteProgress(double percent, string? message = null)
    {
        var bar = GetProgressBar(percent);
        var percentText = $"{percent:F1}%".PadLeft(6);
        
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.Write($"\r{bar} {percentText}");
        
        if (!string.IsNullOrEmpty(message))
        {
            System.Console.Write($" | {message}");
        }
        
        System.Console.ResetColor();
    }

    /// <summary>
    /// Write divider line
    /// </summary>
    public static void WriteDivider(char character = '-', int length = 60)
    {
        System.Console.ForegroundColor = ConsoleColor.DarkGray;
        System.Console.WriteLine(new string(character, length));
        System.Console.ResetColor();
    }

    /// <summary>
    /// Write blank lines
    /// </summary>
    public static void WriteBlankLine(int count = 1)
    {
        for (int i = 0; i < count; i++)
        {
            System.Console.WriteLine();
        }
    }
}
