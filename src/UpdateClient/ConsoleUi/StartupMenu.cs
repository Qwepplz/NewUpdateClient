using System;
using UpdateClient.Config;

namespace UpdateClient.ConsoleUi
{
    internal sealed class StartupMenu
    {
        public bool ShowStartupPrompt(string targetDirectoryPath, RepositoryTarget target)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (target == null) throw new ArgumentNullException(nameof(target));

            Console.WriteLine("UpdateClient updater");
            Console.WriteLine();
            Console.WriteLine("Target folder:");
            Console.WriteLine(targetDirectoryPath);
            Console.WriteLine();
            Console.WriteLine(string.Format("This will sync {0} changes into the current folder.", target.DisplayName));
            Console.WriteLine("Press ENTER to start sync.");
            Console.WriteLine("Press ESC to exit immediately.");
            Console.WriteLine();

            try
            {
                while (true)
                {
                    ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                    if (keyInfo.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine("Starting sync...");
                        Console.WriteLine();
                        return true;
                    }

                    if (keyInfo.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine("Exited by user.");
                        return false;
                    }
                }
            }
            catch
            {
                return true;
            }
        }

        public void PauseBeforeExit()
        {
            try
            {
                if (Environment.UserInteractive && !Console.IsInputRedirected && !Console.IsOutputRedirected)
                {
                    Console.WriteLine();
                    Console.Write("Press any key to continue . . .");
                    Console.ReadKey(true);
                    Console.WriteLine();
                }
            }
            catch
            {
            }
        }
    }
}
