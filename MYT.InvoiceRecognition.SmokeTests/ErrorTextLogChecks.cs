extern alias InvoiceConsole;

using ErrorTextLog = InvoiceConsole::MYT.InvoiceRecognition.Console.ErrorTextLog;

internal static class ErrorTextLogChecks
{
    public static void Check(Action<bool, string> assert)
    {
        var marker = Guid.NewGuid().ToString("N");
        Exception error;
        try
        {
            throw new InvalidOperationException(marker + "-outer",
                new AggregateException(new Exception(marker + "-first"),
                    new Exception(marker + "-second", new Exception(marker + "-deep"))));
        }
        catch (Exception caught) { error = caught; }

        ErrorTextLog.WriteException(error, typeof(ErrorTextLogChecks).GetMethod(nameof(Check)), marker + "-context");
        Parallel.For(0, 32, index => ErrorTextLog.WriteLine($"{marker}-parallel-{index:D2}-end"));

        // Read all hourly files so this check also works across an hour boundary.
        var directory = Path.Combine(AppContext.BaseDirectory, "Log");
        var text = string.Join("\n", Directory.GetFiles(directory, "*.txt").Select(File.ReadAllText));
        assert(text.Contains(error.ToString()) && text.Contains(marker + "-context") &&
            text.Contains("Method: ErrorTextLogChecks.Check"),
            "Text log preserves full exception, stack trace, nested branches, method, and context");
        assert(text.Split('\n').Any(line => line.StartsWith("========================== ") &&
            DateTimeOffset.TryParse(line.Split("========================== ")[1].Split(" ==========================")[0], out _)),
            "Text log entries include a timestamp with offset");
        assert(Enumerable.Range(0, 32).All(index =>
            text.Split($"{marker}-parallel-{index:D2}-end").Length == 2),
            "Concurrent text log writes preserve every entry exactly once");

        ErrorTextLog.WriteLine(new List<string> { marker + "-list-first", marker + "-list-second" });
        var listText = string.Join("\n", Directory.GetFiles(Path.Combine(directory, "MRA"), "*.txt").Select(File.ReadAllText));
        assert(listText.Contains(marker + "-list-first" + Environment.NewLine + marker + "-list-second"),
            "List logging keeps the existing Log/MRA destination");
    }
}
