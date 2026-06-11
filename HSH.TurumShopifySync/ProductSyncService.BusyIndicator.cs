using System;
using System.Threading;
using System.Threading.Tasks;

namespace HSH.TurumShopifySync
{
    internal static partial class ProductSyncService
    {
        private static async Task<T> WithBusyIndicatorAsync<T>(string message, Func<Task<T>> operation, CancellationToken ct)
        {
            if (operation == null)
                throw new ArgumentNullException(nameof(operation));

            var started = DateTime.UtcNow;
            var operationTask = operation();
            var dots = 0;
            var lastTextLength = 0;
            var useInteractiveSpinner = ShouldUseInteractiveSpinner();

            if (useInteractiveSpinner)
                WriteBusyLine(message, ref lastTextLength);
            else
                Console.WriteLine(message + "...");

            while (!operationTask.IsCompleted)
            {
                var delayTask = Task.Delay(TimeSpan.FromMilliseconds(750), ct);
                var completedTask = await Task.WhenAny(operationTask, delayTask);
                if (completedTask == operationTask)
                    break;

                dots = dots == 3 ? 1 : dots + 1;

                if (useInteractiveSpinner)
                    WriteBusyLine(message + new string('.', dots), ref lastTextLength);
            }

            try
            {
                var result = await operationTask;
                if (useInteractiveSpinner)
                    ClearBusyLine(lastTextLength);

                Console.WriteLine(message + " done in " + (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss\.fff"));
                return result;
            }
            catch
            {
                if (useInteractiveSpinner)
                    ClearBusyLine(lastTextLength);

                Console.WriteLine(message + " failed after " + (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss\.fff"));
                throw;
            }
        }

        private static async Task WithBusyIndicatorAsync(string message, Func<Task> operation, CancellationToken ct)
        {
            await WithBusyIndicatorAsync<object>(
                message,
                async () =>
                {
                    await operation();
                    return null;
                },
                ct);
        }

        private static bool ShouldUseInteractiveSpinner()
        {
            if (IsTruthyEnvironmentVariable("GITHUB_ACTIONS") ||
                IsTruthyEnvironmentVariable("CI") ||
                Console.IsOutputRedirected)
            {
                return false;
            }

            return true;
        }

        private static bool IsTruthyEnvironmentVariable(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteBusyLine(string text, ref int lastTextLength)
        {
            var padding = Math.Max(0, lastTextLength - text.Length);
            TeeTextWriter.WriteInteractive("\r" + text + new string(' ', padding));
            lastTextLength = text.Length;
        }

        private static void ClearBusyLine(int lastTextLength)
        {
            TeeTextWriter.WriteInteractive("\r" + new string(' ', lastTextLength) + "\r");
        }
    }
}
