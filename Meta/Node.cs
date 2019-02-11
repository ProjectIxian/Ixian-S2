﻿using DLT.Network;
using IXICore;
using S2;
using S2.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DLT.Meta
{
    class Node
    {
        // Public
        public static S2WalletStorage walletStorage;
        public static WalletState walletState;

        public static UPnP upnp;

        public static StatsConsoleScreen statsConsoleScreen = null;


        public static ulong blockHeight = 0;      // Stores the last known block height 
        public static int blockVersion = 0;      // Stores the last known block version TODO TODO TODO TODO TODO needs to be implemented
        public static IxiNumber balance = 0;      // Stores the last known balance for this node

        public static int keepAliveVersion = 0;

        // Private data
        private static Thread keepAliveThread;
        private static bool autoKeepalive = false;

        private static Thread maintenanceThread;

        public static bool running = false;

        static public void start(bool verboseConsoleOutput)
        {
            running = true;

            // Load or Generate the wallet
            walletStorage = new S2WalletStorage(Config.walletFile);
            if (walletStorage.getPrimaryPublicKey() == null)
            {
                running = false;
                S2.Program.noStart = true;
                return;
            }

            // Initialize the wallet state
            walletState = new WalletState();

            // Setup the stats console
            if (verboseConsoleOutput == false)
            {
                statsConsoleScreen = new StatsConsoleScreen();
            }

            // Network configuration
            upnp = new UPnP();
            if (Config.externalIp != "" && IPAddress.TryParse(Config.externalIp, out _))
            {
                Config.publicServerIP = Config.externalIp;
            }
            else
            {
                Config.publicServerIP = "";
                List<IPAndMask> local_ips = CoreNetworkUtils.GetAllLocalIPAddressesAndMasks();
                foreach (IPAndMask local_ip in local_ips)
                {
                    if (IPv4Subnet.IsPublicIP(local_ip.Address))
                    {
                        Logging.info(String.Format("Public IP detected: {0}, mask {1}.", local_ip.Address.ToString(), local_ip.SubnetMask.ToString()));
                        Config.publicServerIP = local_ip.Address.ToString();
                    }
                }
                if (Config.publicServerIP == "")
                {
                    IPAddress primary_local = CoreNetworkUtils.GetPrimaryIPAddress();
                    if (primary_local == null)
                    {
                        Logging.warn("Unable to determine primary IP address.");
                        showIPmenu();
                    }
                    else
                    {
                        Logging.info(String.Format("None of the locally configured IP addresses are public. Attempting UPnP..."));
                        IPAddress public_ip = upnp.GetExternalIPAddress();
                        if (public_ip == null)
                        {
                            Logging.info("UPnP failed.");
                            showIPmenu();
                        }
                        else
                        {
                            Logging.info(String.Format("UPNP-determined public IP: {0}. Attempting to configure a port-forwarding rule.", public_ip.ToString()));
                            if (upnp.MapPublicPort(Config.serverPort, primary_local))
                            {
                                Config.publicServerIP = public_ip.ToString();
                                Logging.info(string.Format("Network configured. Public IP is: {0}", Config.publicServerIP));
                            }
                            else
                            {
                                Logging.info("UPnP configuration failed.");
                                // Show the IP selector menu
                                showIPmenu();
                            }
                        }
                    }
                }
            }

            PresenceList.generatePresenceList(Config.publicServerIP, 'R');

            // Start the network queue
            NetworkQueue.start();

            // Prepare stats screen
            Config.verboseConsoleOutput = verboseConsoleOutput;
            Logging.consoleOutput = verboseConsoleOutput;
            Logging.flush();
            if (Config.verboseConsoleOutput == false)
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
            NetworkStreamServer.beginNetworkOperations();

            // Start the network client manager
            NetworkClientManager.start();

            // Start the keepalive thread
            autoKeepalive = true;
            keepAliveThread = new Thread(keepAlive);
            keepAliveThread.Start();

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
            // Stop the keepalive thread
            autoKeepalive = false;
            if (keepAliveThread != null)
            {
                keepAliveThread.Abort();
                keepAliveThread = null;
            }

            if (maintenanceThread != null)
            {
                maintenanceThread.Abort();
                maintenanceThread = null;
            }

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
            NetworkStreamServer.stopNetworkOperations();

            // Stop the console stats screen
            if (Config.verboseConsoleOutput == false)
            {
                statsConsoleScreen.stop();
            }
        }

        static public void reconnect()
        {

            // Reset the network receive queue
            NetworkQueue.reset();

            // Check for test client mode
            if (Config.isTestClient)
            {
                TestClientNode.reconnect();
                return;
            }

            // Reconnect server and clients
            NetworkStreamServer.restartNetworkOperations();
            NetworkClientManager.restartClients();
        }

        // Shows an IP selector menu
        static public void showIPmenu()
        {
            Console.WriteLine("This node needs to be reachable from the internet. Please select a valid IP address.");
            Console.WriteLine();

            List<string> ips = CoreNetworkUtils.GetAllLocalIPAddresses();

            uint counter = 0;
            foreach (string ip in ips)
            {
                Console.WriteLine("\t{0}) {1}", counter, ip);
                counter++;
            }
            Console.WriteLine("\tM) Manual Entry");
            Console.WriteLine();

            Console.Write("Choose option [default 0]: ");

            int option = 0;
            try
            {
                string result = Console.ReadLine();
                if (result.Equals("m", StringComparison.OrdinalIgnoreCase))
                {
                    option = -1;
                }
                else
                {
                    option = Convert.ToInt32(result);
                }
            }
            catch (Exception)
            {
                // Handle exceptions
                option = 0;
            }

            if (option == -1)
            {
                showManualIPEntry();
            }
            else
            {
                if (option > ips.Count || option < 0)
                    option = 0;

                string chosenIP = ips[option];
                Config.publicServerIP = chosenIP;
                Console.WriteLine("Using option {0}) {1} as the default external IP for this node.", option, chosenIP);
            }
        }

        static public void showManualIPEntry()
        {
            Console.Write("Type Manual IP: ");
            string chosenIP = Console.ReadLine();

            // Validate the IP
            if (chosenIP.Length > 255 || validateIPv4(chosenIP) == false)
            {
                Console.WriteLine("Incorrect IP. Please try again.");
                showManualIPEntry();
                return;
            }

            Config.publicServerIP = chosenIP;
            Console.WriteLine("Using option M) {0} as the default external IP for this node.", chosenIP);
        }

        // Helper for validating IPv4 addresses
        static private bool validateIPv4(string ipString)
        {
            if (String.IsNullOrWhiteSpace(ipString))
            {
                return false;
            }

            string[] splitValues = ipString.Split('.');
            if (splitValues.Length != 4)
            {
                return false;
            }

            byte tempForParsing;
            return splitValues.All(r => byte.TryParse(r, out tempForParsing));
        }

        // Cleans the storage cache and logs
        public static bool cleanCacheAndLogs()
        {
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

        // Sends perioding keepalive network messages
        private static void keepAlive()
        {
            while (autoKeepalive)
            {
                // Wait x seconds before rechecking
                for (int i = 0; i < CoreConfig.keepAliveInterval; i++)
                {
                    if (autoKeepalive == false)
                    {
                        Thread.Yield();
                        return;
                    }
                    // Sleep for one second
                    Thread.Sleep(1000);
                }

                try
                {
                    // Prepare the keepalive message
                    using (MemoryStream m = new MemoryStream())
                    {
                        using (BinaryWriter writer = new BinaryWriter(m))
                        {
                            writer.Write(keepAliveVersion);

                            byte[] wallet = walletStorage.getPrimaryAddress();
                            writer.Write(wallet.Length);
                            writer.Write(wallet);
                            writer.Write(Config.device_id);

                            // Add the unix timestamp
                            long timestamp = Core.getCurrentTimestamp();
                            writer.Write(timestamp);
                            string hostname = Node.getFullAddress();
                            writer.Write(hostname);

                            // Add a verifiable signature
                            byte[] private_key = walletStorage.getPrimaryPrivateKey();
                            byte[] signature = CryptoManager.lib.getSignature(Encoding.UTF8.GetBytes(CoreConfig.ixianChecksumLockString + "-" + Config.device_id + "-" + timestamp + "-" + hostname), private_key);
                            writer.Write(signature.Length);
                            writer.Write(signature);
                            PresenceList.curNodePresenceAddress.lastSeenTime = timestamp;
                            PresenceList.curNodePresenceAddress.signature = signature;
                        }


                        byte[] address = null;
                        // Update self presence
                        PresenceList.receiveKeepAlive(m.ToArray(), out address);

                        // Send this keepalive message to all connected clients
                        ProtocolMessage.broadcastEventBasedMessage(ProtocolMessageCode.keepAlivePresence, m.ToArray(), address);
                    }
                }
                catch (Exception ex)
                {
                    Logging.error(String.Format("KeepAlive: {0}", ex.Message));
                    continue;
                }

            }

            Thread.Yield();
        }

        public static string getFullAddress()
        {
            return Config.publicServerIP + ":" + Config.serverPort;
        }

        public static ulong getLastBlockHeight()
        {
            return blockHeight;
        }

        public static int getLastBlockVersion()
        {
            return blockVersion;
        }
    }
}
