using IXICore;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using S2.Network;
using System;
using System.Threading;

namespace S2.Meta
{
    class Node: IxianNode
    {
        // Public
        public static WalletStorage walletStorage;
        public static WalletState walletState;

        public static APIServer apiServer;

        public static StatsConsoleScreen statsConsoleScreen = null;


        public static IxiNumber balance = 0;      // Stores the last known balance for this node
        public static ulong blockHeight = 0;

        public static bool forceShutdown = false;

        // Private data
        static Block lastBlock = null;

        private static Thread maintenanceThread;

        public static bool running = false;

        public Node()
        {
            CoreConfig.productVersion = Config.version;
            IxianHandler.setHandler(this);
            init();
        }

        // Perform basic initialization of node
        private void init()
        {


            running = true;

            // Load or Generate the wallet
            if (!initWallet())
            {
                running = false;
                S2.Program.noStart = true;
                return;
            }

            // Setup the stats console
            statsConsoleScreen = new StatsConsoleScreen();

            // Initialize the wallet state
            walletState = new WalletState();
        }

        private bool initWallet()
        {
            walletStorage = new WalletStorage(Config.walletFile);

            Logging.flush();

            if (!walletStorage.walletExists())
            {
                ConsoleHelpers.displayBackupText();

                // Request a password
                // NOTE: This can only be done in testnet to enable automatic testing!
                string password = "";
                if (Config.dangerCommandlinePasswordCleartextUnsafe != "" && CoreConfig.isTestNet)
                {
                    Logging.warn("TestNet detected and wallet password has been specified on the command line!");
                    password = Config.dangerCommandlinePasswordCleartextUnsafe;
                    // Also note that the commandline password still has to be >= 10 characters
                }
                while (password.Length < 10)
                {
                    Logging.flush();
                    password = ConsoleHelpers.requestNewPassword("Enter a password for your new wallet: ");
                    if (forceShutdown)
                    {
                        return false;
                    }
                }
                walletStorage.generateWallet(password);
            }
            else
            {
                ConsoleHelpers.displayBackupText();

                bool success = false;
                while (!success)
                {

                    // NOTE: This is only permitted on the testnet for dev/testing purposes!
                    string password = "";
                    if (Config.dangerCommandlinePasswordCleartextUnsafe != "" && CoreConfig.isTestNet)
                    {
                        Logging.warn("Attempting to unlock the wallet with a password from commandline!");
                        password = Config.dangerCommandlinePasswordCleartextUnsafe;
                    }
                    if (password.Length < 10)
                    {
                        Logging.flush();
                        Console.Write("Enter wallet password: ");
                        password = ConsoleHelpers.getPasswordInput();
                    }
                    if (forceShutdown)
                    {
                        return false;
                    }
                    if (walletStorage.readWallet(password))
                    {
                        success = true;
                    }
                }
            }


            if (walletStorage.getPrimaryPublicKey() == null)
            {
                return false;
            }

            // Wait for any pending log messages to be written
            Logging.flush();

            Console.WriteLine();
            Console.WriteLine("Your IXIAN addresses are: ");
            Console.ForegroundColor = ConsoleColor.Green;
            foreach (var entry in walletStorage.getMyAddressesBase58())
            {
                Console.WriteLine(entry);
            }
            Console.ResetColor();
            Console.WriteLine();

            if (Config.onlyShowAddresses)
            {
                return false;
            }

            // Check if we should change the password of the wallet
            if (Config.changePass == true)
            {
                // Request a new password
                string new_password = "";
                while (new_password.Length < 10)
                {
                    new_password = ConsoleHelpers.requestNewPassword("Enter a new password for your wallet: ");
                    if (forceShutdown)
                    {
                        return false;
                    }
                }
                walletStorage.writeWallet(new_password);
            }

            Logging.info("Public Node Address: {0}", Base58Check.Base58CheckEncoding.EncodePlain(walletStorage.getPrimaryAddress()));


            return true;
        }

        public void start(bool verboseConsoleOutput)
        {
            // Network configuration
            NetworkUtils.configureNetwork(Config.externalIp);

            // Generate presence list
            PresenceList.generatePresenceList(NetworkClientManager.publicIP, Config.serverPort, 'R');

            // Start the network queue
            NetworkQueue.start();

            ActivityStorage.prepareStorage();

            if (Config.apiBinds.Count == 0)
            {
                Config.apiBinds.Add("http://localhost:" + Config.apiPort + "/");
            }

            // Start the HTTP JSON API server
            apiServer = new APIServer(Config.apiBinds, Config.apiUsers, Config.apiAllowedIps);

            // Prepare stats screen
            ConsoleHelpers.verboseConsoleOutput = verboseConsoleOutput;
            Logging.consoleOutput = verboseConsoleOutput;
            Logging.flush();
            if (ConsoleHelpers.verboseConsoleOutput == false)
            {
                statsConsoleScreen.clearScreen();
            }

            // Check for test client mode
            if (Config.isTestClient)
            {
                TestClientNode.start();
                return;
            }

            // Start the node stream server
            NetworkServer.beginNetworkOperations(Config.serverPort);

            // Start the network client manager
            NetworkClientManager.start();

            // Start the keepalive thread
            PresenceList.startKeepAlive();

            // Start the maintenance thread
            maintenanceThread = new Thread(performMaintenance);
            maintenanceThread.Start();
        }

        static public bool update()
        {
            // Update the stream processor
            StreamProcessor.update();

            // Check for test client mode
            if (Config.isTestClient)
            {
                TestClientNode.update();
            }

            return running;
        }

        static public void stop()
        {
            Program.noStart = true;
            forceShutdown = true;
            ConsoleHelpers.forceShutdown = true;

            // Stop the keepalive thread
            PresenceList.stopKeepAlive();

            // Stop the API server
            if (apiServer != null)
            {
                apiServer.stop();
                apiServer = null;
            }

            if (maintenanceThread != null)
            {
                maintenanceThread.Abort();
                maintenanceThread = null;
            }

            ActivityStorage.stopStorage();

            // Stop the network queue
            NetworkQueue.stop();

            // Check for test client mode
            if (Config.isTestClient)
            {
                TestClientNode.stop();
                return;
            }

            // Stop all network clients
            NetworkClientManager.stop();

            // Stop the network server
            NetworkServer.stopNetworkOperations();

            // Stop the console stats screen
            // Console screen has a thread running even if we are in verbose mode
            statsConsoleScreen.stop();
        }

        // Cleans the storage cache and logs
        public static bool cleanCacheAndLogs()
        {
            ActivityStorage.deleteCache();

            PeerStorage.deletePeersFile();

            Logging.clear();

            Logging.info("Cleaned cache and logs.");
            return true;
        }

        // Perform periodic cleanup tasks
        private static void performMaintenance()
        {
            while (running)
            {
                // Sleep a while to prevent cpu usage
                Thread.Sleep(1000);

                // Cleanup the presence list
                PresenceList.performCleanup();
            }
        }

        public override ulong getLastBlockHeight()
        {
            return blockHeight;
        }

        public override ulong getHighestKnownNetworkBlockHeight()
        {
            return getLastBlockHeight();
        }

        public override int getLastBlockVersion()
        {
            if (lastBlock != null)
            {
                return lastBlock.version;
            }
            return 0;
        }

        public override char getNodeType()
        {
            return 'R';
        }

        public override bool isAcceptingConnections()
        {
            // TODO TODO TODO TODO implement this properly
            return true;
        }

        public static void setLastBlock(ulong block_num, byte[] checksum, byte[] ws_checksum, int version)
        {
            Block b = new Block();
            b.blockNum = block_num;
            b.blockChecksum = checksum;
            b.walletStateChecksum = ws_checksum;
            b.version = version;

            lastBlock = b;

            blockHeight = block_num;
        }

        public override Block getLastBlock()
        {
            return lastBlock;
        }

        public override bool addTransaction(Transaction tx)
        {
            throw new NotImplementedException();
        }

        public override Wallet getWallet(byte[] id)
        {
            throw new NotImplementedException();
        }

        public override IxiNumber getWalletBalance(byte[] id)
        {
            throw new NotImplementedException();
        }

        public override void shutdown()
        {
            forceShutdown = true;
        }

        public override WalletStorage getWalletStorage()
        {
            return walletStorage;
        }

        public override void parseProtocolMessage(ProtocolMessageCode code, byte[] data, RemoteEndpoint endpoint)
        {
            ProtocolMessage.parseProtocolMessage(code, data, endpoint);
        }
    }
}
