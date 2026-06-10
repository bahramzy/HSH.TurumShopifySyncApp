using System;
using System.IO;

namespace HSH.TurumShopifySync
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            #region Setup daily file logging

            // ===== Setup daily file logging =====
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;   // bin/Debug eller bin/Release
            var logDir = Path.Combine(baseDir, "logs");

            Directory.CreateDirectory(logDir);

            // Daily file (append whole day)
            var logPath = Path.Combine(
                logDir,
                "TurumSync_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

            var fileWriter = new StreamWriter(logPath, true) { AutoFlush = true };

            Console.SetOut(new TeeTextWriter(Console.Out, fileWriter));
            Console.SetError(new TeeTextWriter(Console.Error, fileWriter));

            Console.WriteLine("=================================================");
            Console.WriteLine("Turum Shopify Sync started: " + DateTime.Now);
            Console.WriteLine("Log file: " + logPath);
            Console.WriteLine("=================================================");

            #endregion

            // .NET Framework: async Main not available (unless newer compiler tricks)

            // Start high-resolution timer
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {

                // Run main flow (synchronously waiting)
                ProductSyncService.RunAsync(SyncSettings.LoadFromEnvironment()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
            finally
            {
                // Always stop and print elapsed time, even after exceptions
                stopwatch.Stop();

                Console.WriteLine();
                Console.WriteLine("=================================================");
                Console.WriteLine("Turum Shopify Sync finished: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                Console.WriteLine("Elapsed time: " + stopwatch.Elapsed.ToString(@"hh\:mm\:ss\.fff"));
                Console.WriteLine("Elapsed milliseconds: " + stopwatch.ElapsedMilliseconds + " ms");
                Console.WriteLine("=================================================");
            }
        }

    }
}
