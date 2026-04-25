using System;
using UpdateClient.Config;

namespace UpdateClient.ConsoleUi
{
    internal sealed class StartupMenu
    {
        public string ShowStartupPrompt(string targetDirectoryPath, RepositoryTarget target)
        {
            if (string.IsNullOrWhiteSpace(targetDirectoryPath)) throw new ArgumentException("Value cannot be empty.", nameof(targetDirectoryPath));
            if (target == null) throw new ArgumentNullException(nameof(target));

            try
            {
                while (true)
                {
                    this.ShowMainMenu(targetDirectoryPath);
                    ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                    if (keyInfo.Key == ConsoleKey.D1 || keyInfo.Key == ConsoleKey.NumPad1 || keyInfo.Key == ConsoleKey.Enter)
                    {
                        return StartSync("stable", AppOptions.StableBranchName);
                    }

                    if (keyInfo.Key == ConsoleKey.D2 || keyInfo.Key == ConsoleKey.NumPad2)
                    {
                        string advancedBranch = this.ShowAdvancedMenu();
                        if (!string.IsNullOrWhiteSpace(advancedBranch))
                        {
                            return advancedBranch;
                        }
                    }

                    if (keyInfo.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine("Exited by user.");
                        return null;
                    }
                }
            }
            catch
            {
                return StartSync("stable", AppOptions.StableBranchName);
            }
        }

        private void ShowMainMenu(string targetDirectoryPath)
        {
            Console.WriteLine("UpdateClient updater");
            Console.WriteLine();
            Console.WriteLine("Target folder:");
            Console.WriteLine(targetDirectoryPath);
            Console.WriteLine();
            Console.WriteLine("Select sync channel:");
            Console.WriteLine("  1. Stable version (main)");
            Console.WriteLine("  2. Advanced options");
            Console.WriteLine();
            Console.WriteLine("Press ENTER to sync stable version.");
            Console.WriteLine("Press ESC to exit immediately.");
            Console.WriteLine();
        }

        private string ShowAdvancedMenu()
        {
            Console.WriteLine("Advanced options");
            Console.WriteLine();
            Console.WriteLine("Select sync channel:");
            Console.WriteLine("  1. Development version (dev)");
            Console.WriteLine();
            Console.WriteLine("Press ESC to return to the main menu.");
            Console.WriteLine();

            while (true)
            {
                ConsoleKeyInfo keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.D1 || keyInfo.Key == ConsoleKey.NumPad1)
                {
                    return StartSync("development", AppOptions.DevelopmentBranchName);
                }

                if (keyInfo.Key == ConsoleKey.Escape)
                {
                    Console.WriteLine("Returning to main menu...");
                    Console.WriteLine();
                    return null;
                }
            }
        }

        private static string StartSync(string channelName, string branchName)
        {
            Console.WriteLine(string.Format("Starting {0} sync from branch: {1}", channelName, branchName));
            Console.WriteLine();
            return branchName;
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
