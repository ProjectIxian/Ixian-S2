using IXICore;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using S2.Network;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Activity = IXICore.Meta.Activity;

namespace S2.Meta
{
    class Balance
    {
        public Address address = null;
        public IxiNumber balance = 0;
        public ulong blockHeight = 0;
        public byte[] blockChecksum = null;
        public bool verified = false;
        public long lastUpdate = 0;
    }

    class Node : IxianNode
    {
        public static APIServer apiServer;

        public static StatsConsoleScreen statsConsoleScreen = null;


        public static Balance balance = new Balance();      // Stores the last known balance for this node

        public static TransactionInclusion tiv = null;

        // Private data
        private static Thread maintenanceThread;

        private static bool running = false;

        private static ulong networkBlockHeight = 0;
        private static byte[] networkBlockChecksum = null;
        private static int networkBlockVersion = 0;
        private bool generatedNewWallet = false;

        public Node()
        {
            CoreConfig.simultaneousConnectedNeighbors = 6;
            IxianHandler.init(Config.version, this, Config.networkType, true);
            init();
        }

        // Perform basic initialization of node
        private void init()
        {
            running = true;

            CoreConfig.maximumServerMasterNodes = Config.maximumStreamClients;
            CoreConfig.maximumServerClients = Config.maximumStreamClients;

            UpdateVerify.init(Config.checkVersionUrl, Config.checkVersionSeconds);

            // Network configuration
            NetworkUtils.configureNetwork(Config.externalIp, Config.serverPort);

            // Load or Generate the wallet
            if (!initWallet())
            {
                running = false;
                S2.Program.noStart = true;
                return;
            }

            // Setup the stats console
            statsConsoleScreen = new StatsConsoleScreen();

            PeerStorage.init("");

            // Init TIV
            tiv = new TransactionInclusion();
        }

        private bool initWallet()
        {
            WalletStorage walletStorage = new WalletStorage(Config.walletFile);

            Logging.flush();

            if (!walletStorage.walletExists())
            {
                ConsoleHelpers.displayBackupText();

                // Request a password
                // NOTE: This can only be done in testnet to enable automatic testing!
                string password = "";
                if (Config.dangerCommandlinePasswordCleartextUnsafe != "")
                {
                    Logging.warn("TestNet detected and wallet password has been specified on the command line!");
                    password = Config.dangerCommandlinePasswordCleartextUnsafe;
                    // Also note that the commandline password still has to be >= 10 characters
                }
                while (password.Length < 10)
                {
                    Logging.flush();
                    password = ConsoleHelpers.requestNewPassword("Enter a password for your new wallet: ");
                    if (IxianHandler.forceShutdown)
                    {
                        return false;
                    }
                }
                walletStorage.generateWallet(password);
                generatedNewWallet = true;
            }
            else
            {
                ConsoleHelpers.displayBackupText();

                bool success = false;
                while (!success)
                {

                    // NOTE: This is only permitted on the testnet for dev/testing purposes!
                    string password = "";
                    if (Config.dangerCommandlinePasswordCleartextUnsafe != "")
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
                    if (IxianHandler.forceShutdown)
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
                    if (IxianHandler.forceShutdown)
                    {
                        return false;
                    }
                }
                walletStorage.writeWallet(new_password);
            }

            Logging.info("Public Node Address: {0}", walletStorage.getPrimaryAddress().ToString());


            if (walletStorage.viewingWallet)
            {
                Logging.error("Viewing-only wallet {0} cannot be used as the primary DLT Node wallet.", walletStorage.getPrimaryAddress().ToString());
                return false;
            }

            IxianHandler.addWallet(walletStorage);

            return true;
        }

        public void start(bool verboseConsoleOutput)
        {
            UpdateVerify.start();

            // Generate presence list
            PresenceList.init(IxianHandler.publicIP, Config.serverPort, 'R');

            // Start the network queue
            NetworkQueue.start();

            ActivityStorage.prepareStorage();

            if (Config.apiBinds.Count == 0)
            {
                Config.apiBinds.Add("http://localhost:" + Config.apiPort + "/");
            }

            // Start the HTTP JSON API server
            apiServer = new APIServer(Config.apiBinds, Config.apiUsers, Config.apiAllowedIps);

            if (IXICore.Platform.onWindows() == true && !Config.disableWebStart)
            {
                Process.Start(new ProcessStartInfo(Config.apiBinds[0]) { UseShellExecute = true });
            }

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
            NetworkServer.beginNetworkOperations();

            // Start the network client manager
            NetworkClientManager.start(2);

            // Start the keepalive thread
            PresenceList.startKeepAlive();

            // Start TIV
            if (generatedNewWallet || !File.Exists(Config.walletFile))
            {
                generatedNewWallet = false;
                tiv.start("", false, false);
            }else
            {
                tiv.start("", 0, null, false, false);
            }

            // Start the maintenance thread
            maintenanceThread = new Thread(performMaintenance);
            maintenanceThread.Start();
        }

        static public bool update()
        {
            // Update the stream processor
            StreamProcessor.update();

            // Request initial wallet balance
            if (balance.blockHeight == 0 || balance.lastUpdate + 300 < Clock.getTimestamp())
            {
                using (MemoryStream mw = new MemoryStream())
                {
                    using (BinaryWriter writer = new BinaryWriter(mw))
                    {
                        writer.WriteIxiVarInt(IxianHandler.getWalletStorage().getPrimaryAddress().addressWithChecksum.Length);
                        writer.Write(IxianHandler.getWalletStorage().getPrimaryAddress().addressWithChecksum);
                        NetworkClientManager.broadcastData(new char[] { 'M', 'H' }, ProtocolMessageCode.getBalance2, mw.ToArray(), null);
                    }
                }
            }

            if (IxianHandler.status != NodeStatus.warmUp)
            {
                if (Clock.getTimestamp() - BlockHeaderStorage.lastBlockHeaderTime > 1800) // if no block for over 1800 seconds
                {
                    IxianHandler.status = NodeStatus.stalled;
                }
            }

            return running;
        }

        static public void stop()
        {
            Program.noStart = true;
            IxianHandler.forceShutdown = true;

            UpdateVerify.stop();

            // Stop TIV
            tiv.stop();

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
                maintenanceThread.Interrupt();
                maintenanceThread.Join();
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

        public override bool isAcceptingConnections()
        {
            // TODO TODO TODO TODO implement this properly
            return true;
        }


        static public void setNetworkBlock(ulong block_height, byte[] block_checksum, int block_version)
        {
            networkBlockHeight = block_height;
            networkBlockChecksum = block_checksum;
            networkBlockVersion = block_version;
        }

        public override void receivedTransactionInclusionVerificationResponse(byte[] txid, bool verified)
        {
            // TODO implement error
            // TODO implement blocknum

            ActivityStatus status = ActivityStatus.Pending;
            if (verified)
            {
                status = ActivityStatus.Final;
                PendingTransactions.remove(txid);
            }

            ActivityStorage.updateStatus(txid, status, 0);
        }

        public override void receivedBlockHeader(Block block_header, bool verified)
        {
            if (balance.blockChecksum != null && balance.blockChecksum.SequenceEqual(block_header.blockChecksum))
            {
                balance.verified = true;
            }
            if (block_header.blockNum >= networkBlockHeight)
            {
                IxianHandler.status = NodeStatus.ready;
                setNetworkBlock(block_header.blockNum, block_header.blockChecksum, block_header.version);
            }
            processPendingTransactions();
        }

        public override ulong getLastBlockHeight()
        {
            if (tiv.getLastBlockHeader() == null)
            {
                return 0;
            }
            return tiv.getLastBlockHeader().blockNum;
        }

        public override ulong getHighestKnownNetworkBlockHeight()
        {
            return networkBlockHeight;
        }

        public override int getLastBlockVersion()
        {
            if (tiv.getLastBlockHeader() == null
                || tiv.getLastBlockHeader().version < Block.maxVersion)
            {
                // TODO Omega force to v10 after upgrade
                return Block.maxVersion - 1;
            }
            return tiv.getLastBlockHeader().version;
        }

        public override bool addTransaction(Transaction tx, bool force_broadcast)
        {
            // TODO Send to peer if directly connectable
            CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'M', 'H' }, ProtocolMessageCode.transactionData2, tx.getBytes(true, true), null);
            PendingTransactions.addPendingLocalTransaction(tx);
            return true;
        }

        public override Block getLastBlock()
        {
            return tiv.getLastBlockHeader();
        }

        public override Wallet getWallet(Address id)
        {
            // TODO Properly implement this for multiple addresses
            if(balance.address != null && id.addressNoChecksum.SequenceEqual(balance.address.addressNoChecksum))
            {
                return new Wallet(balance.address, balance.balance);
            }
            return new Wallet(id, 0);
        }

        public override IxiNumber getWalletBalance(Address id)
        {
            // TODO Properly implement this for multiple addresses
            if (balance.address != null && id.addressNoChecksum.SequenceEqual(balance.address.addressNoChecksum))
            {
                return balance.balance;
            }
            return 0;
        }

        public override void shutdown()
        {
            IxianHandler.forceShutdown = true;
        }

        public override void parseProtocolMessage(ProtocolMessageCode code, byte[] data, RemoteEndpoint endpoint)
        {
            ProtocolMessage.parseProtocolMessage(code, data, endpoint);
        }

        public static void addTransactionToActivityStorage(Transaction transaction)
        {
            Activity activity = null;
            int type = -1;
            IxiNumber value = transaction.amount;
            List<byte[]> wallet_list = null;
            Address wallet = null;
            Address primary_address = transaction.pubKey;
            if (IxianHandler.getWalletStorage().isMyAddress(primary_address))
            {
                wallet = primary_address;
                type = (int)ActivityType.TransactionSent;
                if (transaction.type == (int)Transaction.Type.PoWSolution)
                {
                    type = (int)ActivityType.MiningReward;
                    value = ConsensusConfig.calculateMiningRewardForBlock(transaction.powSolution.blockNum);
                }
            }
            else
            {
                wallet_list = IxianHandler.getWalletStorage().extractMyAddressesFromAddressList(transaction.toList);
                if (wallet_list != null)
                {
                    type = (int)ActivityType.TransactionReceived;
                    if (transaction.type == (int)Transaction.Type.StakingReward)
                    {
                        type = (int)ActivityType.StakingReward;
                    }
                }
            }
            if (type != -1)
            {
                int status = (int)ActivityStatus.Pending;
                if (transaction.applied > 0)
                {
                    status = (int)ActivityStatus.Final;
                }
                if (wallet_list != null)
                {
                    foreach (var entry in wallet_list)
                    {
                        activity = new Activity(IxianHandler.getWalletStorage().getSeedHash(), Base58Check.Base58CheckEncoding.EncodePlain(entry), Base58Check.Base58CheckEncoding.EncodePlain(primary_address.addressNoChecksum), transaction.toList, type, transaction.id, transaction.toList.First(x => x.Key.addressNoChecksum.SequenceEqual(entry)).ToString(), transaction.timeStamp, status, transaction.applied, transaction.getTxIdString());
                        ActivityStorage.insertActivity(activity);
                    }
                }
                else if (wallet != null)
                {
                    activity = new Activity(IxianHandler.getWalletStorage().getSeedHash(), Base58Check.Base58CheckEncoding.EncodePlain(wallet.addressNoChecksum), Base58Check.Base58CheckEncoding.EncodePlain(primary_address.addressNoChecksum), transaction.toList, type, transaction.id, value.ToString(), transaction.timeStamp, status, transaction.applied, transaction.getTxIdString());
                    ActivityStorage.insertActivity(activity);
                }
            }
        }

        public static void processPendingTransactions()
        {
            // TODO TODO improve to include failed transactions
            ulong last_block_height = IxianHandler.getLastBlockHeight();
            lock (PendingTransactions.pendingTransactions)
            {
                long cur_time = Clock.getTimestamp();
                List<PendingTransaction> tmp_pending_transactions = new List<PendingTransaction>(PendingTransactions.pendingTransactions);
                int idx = 0;
                foreach (var entry in tmp_pending_transactions)
                {
                    Transaction t = entry.transaction;
                    long tx_time = entry.addedTimestamp;

                    if (t.applied != 0)
                    {
                        PendingTransactions.pendingTransactions.RemoveAll(x => x.transaction.id.SequenceEqual(t.id));
                        continue;
                    }

                    // if transaction expired, remove it from pending transactions
                    if (last_block_height > ConsensusConfig.getRedactedWindowSize() && t.blockHeight < last_block_height - ConsensusConfig.getRedactedWindowSize())
                    {
                        ActivityStorage.updateStatus(t.id, ActivityStatus.Error, 0);
                        PendingTransactions.pendingTransactions.RemoveAll(x => x.transaction.id.SequenceEqual(t.id));
                        continue;
                    }

                    if (cur_time - tx_time > 40) // if the transaction is pending for over 40 seconds, resend
                    {
                        CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'M', 'H' }, ProtocolMessageCode.transactionData2, t.getBytes(true, true), null);
                        entry.addedTimestamp = cur_time;
                        entry.confirmedNodeList.Clear();
                    }

                    if (entry.confirmedNodeList.Count() > 3) // already received 3+ feedback
                    {
                        continue;
                    }

                    if (cur_time - tx_time > 20) // if the transaction is pending for over 20 seconds, send inquiry
                    {
                        CoreProtocolMessage.broadcastGetTransaction(t.id, 0, null, false);
                    }

                    idx++;
                }
            }
        }

        public override Block getBlockHeader(ulong blockNum)
        {
            return BlockHeaderStorage.getBlockHeader(blockNum);
        }

        public override IxiNumber getMinSignerPowDifficulty(ulong blockNum)
        {
            // TODO TODO implement this properly
            return ConsensusConfig.minBlockSignerPowDifficulty;
        }
    }
}
