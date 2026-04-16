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

        public bool ShowMirrorConfirmation(RepositoryTarget target, string githubFailureMessage)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));

            Console.WriteLine("GitHub is unavailable for this sync.");
            if (!string.IsNullOrWhiteSpace(githubFailureMessage))
            {
                Console.WriteLine("GitHub error:");
                Console.WriteLine(githubFailureMessage);
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("You can continue with the Gitee mirror for {0}.", target.DisplayName));
            Console.WriteLine("Risk: the mirror may lag behind GitHub.");
            Console.WriteLine("Continuing may sync an older version, miss newer files, or remove files that only exist in newer GitHub versions.");
            Console.WriteLine();
            Console.Write("Type YES to continue with the mirror, or press ENTER to cancel: ");
            try
            {
                string input = Console.ReadLine();
                Console.WriteLine();

                if (string.Equals((input ?? string.Empty).Trim(), "YES", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Mirror sync confirmed.");
                    Console.WriteLine();
                    return true;
                }

                Console.WriteLine("Mirror sync canceled.");
                return false;
            }
            catch
            {
                Console.WriteLine();
                return false;
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
