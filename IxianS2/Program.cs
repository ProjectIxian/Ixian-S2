using DLT;
using DLT.Meta;
using System;
using System.Timers;
using System.Threading;
using IXICore.Utils;
using IXICore;
using DLT.Network;
using System.IO;
using System.Diagnostics;

namespace S2
{
    class Program
    {
        private static System.Timers.Timer mainLoopTimer;

        public static bool noStart = false;

        private static Node node = null;

        static void checkRequiredFiles()
        {
            string[] critical_dlls =
            {
                "BouncyCastle.Crypto.dll",
                "FluentCommandLineParser.dll",
                "Newtonsoft.Json.dll",
                "Open.Nat.dll",
                "SQLite-net.dll",
                "SQLitePCLRaw.batteries_green.dll",
                "SQLitePCLRaw.batteries_v2.dll",
                "SQLitePCLRaw.core.dll",
                "SQLitePCLRaw.provider.e_sqlite3.dll"
            };

            foreach (string critical_dll in critical_dlls)
            {
                if (!File.Exists(critical_dll))
                {
                    Logging.error(String.Format("Missing '{0}' in the program folder. Possibly the IXIAN archive was corrupted or incorrectly installed. Please re-download the archive from https://www.ixian.io!", critical_dll));
                    Logging.info("Press ENTER to exit.");
                    Console.ReadLine();
                    Environment.Exit(-1);
                }
            }

            // Special case for sqlite3
            if (!File.Exists("x64" + Path.DirectorySeparatorChar + "e_sqlite3.dll") && !File.Exists("libe_sqlite3.so") && !File.Exists("libe_sqlite3.dylib"))
            {
                Logging.error(String.Format("Missing '{0}' in the program folder. Possibly the IXIAN archive was corrupted or incorrectly installed. Please re-download the archive from https://www.ixian.io!", "sqlite3"));
                Logging.info("Press ENTER to exit.");
                Console.ReadLine();
                Environment.Exit(-1);
            }

        }
        static void checkVCRedist()
        {
            object installed_vc_redist = Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\x64", "Installed", 0);
            object installed_vc_redist_debug = Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\debug\\x64", "Installed", 0);
            bool success = false;
            if ((installed_vc_redist is int && (int)installed_vc_redist > 0) || (installed_vc_redist_debug is int && (int)installed_vc_redist_debug > 0))
            {
                Logging.info("Visual C++ 2017 (v141) redistributable is already installed.");
                success = true;
            }
            else
            {
                if (!File.Exists("vc_redist.x64.exe"))
                {
                    Logging.warn("The VC++2017 redistributable file is not found. Please download the v141 version of the Visual C++ 2017 redistributable and install it manually!");
                    Logging.flush();
                    Console.WriteLine("You can download it from this URL:");
                    Console.WriteLine("https://visualstudio.microsoft.com/downloads/");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine("NOTICE: In order to run this IXIAN node, Visual Studio 2017 Redistributable (v141) must be installed.");
                    Console.WriteLine("This can be done automatically by IXIAN, or, you can install it manually from this URL:");
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("https://visualstudio.microsoft.com/downloads/");
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.WriteLine("The installer may open a UAC (User Account Control) prompt. Please verify that the executable is signed by Microsoft Corporation before allowing it to install!");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("Automatically install Visual C++ 2017 redistributable? (Y/N): ");
                    ConsoleKeyInfo k = Console.ReadKey();
                    Console.WriteLine();
                    Console.WriteLine();
                    if (k.Key == ConsoleKey.Y)
                    {
                        Logging.info("Installing Visual C++ 2017 (v141) redistributable...");
                        ProcessStartInfo installer = new ProcessStartInfo("vc_redist.x64.exe");
                        installer.Arguments = "/install /passive /norestart";
                        installer.LoadUserProfile = false;
                        installer.RedirectStandardError = true;
                        installer.RedirectStandardInput = true;
                        installer.RedirectStandardOutput = true;
                        installer.UseShellExecute = false;
                        Logging.info("Starting installer. Please allow up to one minute for installation...");
                        Process p = Process.Start(installer);
                        while (!p.HasExited)
                        {
                            if (!p.WaitForExit(60000))
                            {
                                Logging.info("The install process seems to be stuck. Terminate? (Y/N): ");
                                k = Console.ReadKey();
                                if (k.Key == ConsoleKey.Y)
                                {
                                    Logging.warn("Terminating installer process...");
                                    p.Kill();
                                    Logging.warn(String.Format("Process output: {0}", p.StandardOutput.ReadToEnd()));
                                    Logging.warn(String.Format("Process error output: {0}", p.StandardError.ReadToEnd()));
                                }
                            }
                        }
                        installed_vc_redist = Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\x64", "Installed", 0);
                        installed_vc_redist_debug = Microsoft.Win32.Registry.GetValue("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\VisualStudio\\14.0\\VC\\Runtimes\\debug\\x64", "Installed", 0);
                        if ((installed_vc_redist is int && (int)installed_vc_redist > 0) || (installed_vc_redist_debug is int && (int)installed_vc_redist_debug > 0))
                        {
                            Logging.info("Visual C++ 2017 (v141) redistributable has installed successfully.");
                            success = true;
                        }
                        else
                        {
                            Logging.info("Visual C++ 2017 has failed to install. Please review the error text (if any) and install manually:");
                            Logging.warn(String.Format("Process exit code: {0}.", p.ExitCode));
                            Logging.warn(String.Format("Process output: {0}", p.StandardOutput.ReadToEnd()));
                            Logging.warn(String.Format("Process error output: {0}", p.StandardError.ReadToEnd()));
                        }
                    }
                }
            }
            if (!success)
            {
                Logging.info("IXIAN requires the Visual Studio 2017 runtime for normal operation. Please ensure it is installed and then restart the program!");
                Logging.info("Press ENTER to exit.");
                Console.ReadLine();
                Environment.Exit(-1);
            }
        }

        static void Main(string[] args)
        {
            // Clear the console first
            Console.Clear();

            IXICore.Utils.ConsoleHelpers.prepareWindowsConsole();

            // Start logging
            Logging.start();

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e) {
                ConsoleHelpers.verboseConsoleOutput = true;
                Logging.consoleOutput = ConsoleHelpers.verboseConsoleOutput;
                e.Cancel = true;
                Node.forceShutdown = true;
            };

            onStart(args);

            if (Node.apiServer != null)
            {
                while (Node.forceShutdown == false)
                {
                    Thread.Sleep(1000);
                }
            }
            onStop();

        }

        static void onStart(string[] args)
        {
            bool verboseConsoleOutputSetting = ConsoleHelpers.verboseConsoleOutput;
            ConsoleHelpers.verboseConsoleOutput = true;

            Console.WriteLine(string.Format("IXIAN S2 {0} ({1})", Config.version, CoreConfig.version));

            // Check for critical files in the exe dir
            checkRequiredFiles();

            // Read configuration from command line
            Config.readFromCommandLine(args);

            if (noStart)
            {
                Thread.Sleep(1000);
                return;
            }

            // Set the logging options
            Logging.setOptions(Config.maxLogSize, Config.maxLogCount);

            Logging.info(string.Format("Starting IXIAN S2 {0} ({1})", Config.version, CoreConfig.version));

            // Check for the right vc++ redist for the argon miner
            // Ignore if we're on Mono
            if (IXICore.Platform.onMono() == false)
            {
                Logging.info("Runtime environment is .NET");
                checkVCRedist();
            }
            else
            {
                Logging.info("Runtime environment is Mono");
            }

            // Log the parameters to notice any changes
            Logging.info(String.Format("Mainnet: {0}", !CoreConfig.isTestNet));
            Logging.info(String.Format("Server Port: {0}", NetworkServer.listeningPort));
            Logging.info(String.Format("API Port: {0}", Config.apiPort));
            Logging.info(String.Format("Wallet File: {0}", Config.walletFile));

            // Initialize the crypto manager
            CryptoManager.initLib();

            // Initialize the node
            node = new Node();

            // Start the actual S2 node
            node.start(verboseConsoleOutputSetting);

            if (noStart)
            {
                Thread.Sleep(1000);
                return;
            }

            // Setup a timer to handle routine updates
            mainLoopTimer = new System.Timers.Timer(1000);
            mainLoopTimer.Elapsed += new ElapsedEventHandler(onUpdate);
            mainLoopTimer.Start();

            if (ConsoleHelpers.verboseConsoleOutput)
                Console.WriteLine("-----------\nPress Ctrl-C or use the /shutdown API to stop the S2 process at any time.\n");
        }

        static void onUpdate(object source, ElapsedEventArgs e)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey();

                if (key.Key == ConsoleKey.W)
                {
                    string ws_checksum = Crypto.hashToString(Node.walletState.calculateWalletStateChecksum());
                    Logging.info(String.Format("WalletState checksum: ({0} wallets, {1} snapshots) : {2}",
                        Node.walletState.numWallets, Node.walletState.hasSnapshot, ws_checksum));
                }
                else if (key.Key == ConsoleKey.V)
                {
                    ConsoleHelpers.verboseConsoleOutput = !ConsoleHelpers.verboseConsoleOutput;
                    Logging.consoleOutput = ConsoleHelpers.verboseConsoleOutput;
                    Console.CursorVisible = ConsoleHelpers.verboseConsoleOutput;
                    if (ConsoleHelpers.verboseConsoleOutput == false)
                        Node.statsConsoleScreen.clearScreen();
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    ConsoleHelpers.verboseConsoleOutput = true;
                    Logging.consoleOutput = ConsoleHelpers.verboseConsoleOutput;
                    Node.forceShutdown = true;
                }

            }
            if (Node.update() == false)
            {
                Node.forceShutdown = true;
            }
        }

        static void onStop()
        {
            if (mainLoopTimer != null)
            {
                mainLoopTimer.Stop();
            }

            if (noStart == false)
            {
                // Stop the DLT
                Node.stop();
            }

            // Stop logging
            Logging.flush();
            Logging.stop();

            if (noStart == false)
            {
                Console.WriteLine("");
                Console.WriteLine("Ixian S2 Node stopped.");
            }
        }
    }
}
