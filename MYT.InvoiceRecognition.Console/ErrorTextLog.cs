using System.Globalization;
using System.Reflection;
using System.Text;

namespace MYT.InvoiceRecognition.Console
{
    public static class ErrorTextLog
    {
        private static readonly object WriteLock = new();

        public static string GetMethodName(MethodBase? mBase) =>
            mBase == null ? string.Empty : $"{mBase.ReflectedType?.FullName}.{mBase.Name}";

        public static void WriteLine(string message, MethodBase? mBase = null)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            WriteEntry(message, mBase);
        }

        // Exception.ToString() includes the exception type, stack trace, and nested exceptions.
        public static void WriteException(Exception ex, MethodBase? mBase = null, string? context = null)
        {
            WriteEntry(string.IsNullOrWhiteSpace(context) ? ex.ToString() : $"{context}{Environment.NewLine}{ex}", mBase);
        }

        public static void WriteLine(List<string> messageList)
        {
            if (messageList.Count == 0) return;
            WriteEntry(string.Join(Environment.NewLine, messageList));
        }

        private static void WriteEntry(string message, MethodBase? mBase = null)
        {
            try
            {
                // Keep each entry together when multiple threads log at the same time.
                lock (WriteLock)
                {
                    var timestamp = DateTimeOffset.Now;
                    var directory = Path.Combine(AppContext.BaseDirectory, "Log");
                    Directory.CreateDirectory(directory);
                    var file = Path.Combine(directory, timestamp.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture) + ".txt");
                    using var writer = new StreamWriter(file, append: true, encoding: new UTF8Encoding(false));
                    writer.WriteLine($"========================== {timestamp:O} ==========================");
                    var method = GetMethodName(mBase);
                    if (method.Length > 0) writer.WriteLine($"Method: {method}");
                    writer.WriteLine(message);
                    writer.WriteLine();
                }
            }
            catch (Exception logError)
            {
                // A file-system failure must not replace the original processing error.
                try
                {
                    global::System.Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] Unable to write error text log: {logError.Message}");
                    global::System.Console.Error.WriteLine(message);
                }
                catch (Exception)
                {
                    // stderr can also be unavailable, for example when its pipe is closed.
                }
            }
        }
    }
}
