using IXICore;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using S2.Meta;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace S2.Network
{
    public class ProtocolMessage
    {
        public static ProtocolMessageCode waitingFor = 0;
        public static bool blocked = false;

        public static void setWaitFor(ProtocolMessageCode value)
        {
            waitingFor = value;
            blocked = true;
        }

        public static void wait(int timeout_seconds)
        {
            DateTime start = DateTime.UtcNow;
            while (blocked)
            {
                if ((DateTime.UtcNow - start).TotalSeconds > timeout_seconds)
                {
                    Logging.warn("Timeout occured while waiting for " + waitingFor);
                    break;
                }
                Thread.Sleep(250);
            }
        }
        
        // Unified protocol message parsing
        public static void parseProtocolMessage(ProtocolMessageCode code, byte[] data, RemoteEndpoint endpoint)
        {
            if (endpoint == null)
            {
                Logging.error("Endpoint was null. parseProtocolMessage");
                return;
            }
            try
            {
                switch (code)
                {
                    case ProtocolMessageCode.hello:
                        using (MemoryStream m = new MemoryStream(data))
                        {
                            using (BinaryReader reader = new BinaryReader(m))
                            {
                                if (CoreProtocolMessage.processHelloMessage(endpoint, reader))
                                {
                                    int challenge_len = reader.ReadInt32();
                                    byte[] challenge = reader.ReadBytes(challenge_len);

                                    byte[] challenge_response = CryptoManager.lib.getSignature(challenge, Node.walletStorage.getPrimaryPrivateKey());

                                    CoreProtocolMessage.sendHelloMessage(endpoint, true, challenge_response);
                                    endpoint.helloReceived = true;
                                    return;
                                }
                            }
                        }
                        break;


                    case ProtocolMessageCode.helloData:
                        using (MemoryStream m = new MemoryStream(data))
                        {
                            using (BinaryReader reader = new BinaryReader(m))
                            {
                                if (CoreProtocolMessage.processHelloMessage(endpoint, reader))
                                {
                                    char node_type = endpoint.presenceAddress.type;
                                    if (node_type != 'M' && node_type != 'H')
                                    {
                                        CoreProtocolMessage.sendBye(endpoint, ProtocolByeCode.expectingMaster, string.Format("Expecting master node."), "", true);
                                        return;
                                    }

                                    ulong last_block_num = reader.ReadUInt64();

                                    int bcLen = reader.ReadInt32();
                                    byte[] block_checksum = reader.ReadBytes(bcLen);

                                    int wsLen = reader.ReadInt32();
                                    byte[] walletstate_checksum = reader.ReadBytes(wsLen);

                                    int consensus = reader.ReadInt32();

                                    endpoint.blockHeight = last_block_num;

                                    int block_version = reader.ReadInt32();

                                    // Check for legacy level
                                    ulong legacy_level = reader.ReadUInt64(); // deprecated

                                    int challenge_response_len = reader.ReadInt32();
                                    byte[] challenge_response = reader.ReadBytes(challenge_response_len);
                                    if (!CryptoManager.lib.verifySignature(endpoint.challenge, endpoint.serverPubKey, challenge_response))
                                    {
                                        CoreProtocolMessage.sendBye(endpoint, ProtocolByeCode.authFailed, string.Format("Invalid challenge response."), "", true);
                                        return;
                                    }

                                    // Process the hello data
                                    endpoint.helloReceived = true;
                                    NetworkClientManager.recalculateLocalTimeDifference();

                                    Node.setNetworkBlock(last_block_num, block_checksum, block_version);

                                    // Get random presences
                                    endpoint.sendData(ProtocolMessageCode.getRandomPresences, new byte[1] { (byte)'M' });

                                    CoreProtocolMessage.subscribeToEvents(endpoint);
                                }
                            }
                        }
                        break;

                    case ProtocolMessageCode.s2data:
                        {
                            StreamProcessor.receiveData(data, endpoint);
                        }
                        break;

                    case ProtocolMessageCode.s2failed:
                        {
                            using (MemoryStream m = new MemoryStream(data))
                            {
                                using (BinaryReader reader = new BinaryReader(m))
                                {
                                    Logging.error("Failed to send s2 data");
                                }
                            }
                        }
                        break;

                    case ProtocolMessageCode.s2signature:
                        {
                            StreamProcessor.receivedTransactionSignature(data, endpoint);
                        }
                        break;

                    case ProtocolMessageCode.newTransaction:
                    case ProtocolMessageCode.transactionData:
                        {
                            // Forward the new transaction message to the DLT network
                            CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'M', 'H' }, ProtocolMessageCode.newTransaction, data, null);

                            Transaction tx = new Transaction(data, true);

                            PendingTransactions.increaseReceivedCount(tx.id);

                            Node.tiv.receivedNewTransaction(tx);
                            Logging.info("Received new transaction {0}", tx.id);

                            Node.addTransactionToActivityStorage(tx);
                        }
                        break;

                    /*case ProtocolMessageCode.presenceList:
                        {
                            Logging.info("Receiving complete presence list");
                            PresenceList.syncFromBytes(data);
                        }
                        break;*/

                    case ProtocolMessageCode.updatePresence:
                        {
                            // Parse the data and update entries in the presence list
                            PresenceList.updateFromBytes(data);
                        }
                        break;


                    case ProtocolMessageCode.keepAlivePresence:
                        {
                            byte[] address = null;
                            bool updated = PresenceList.receiveKeepAlive(data, out address, endpoint);
                        }
                        break;

                    case ProtocolMessageCode.getPresence:
                        {
                            using (MemoryStream m = new MemoryStream(data))
                            {
                                using (BinaryReader reader = new BinaryReader(m))
                                {
                                    int walletLen = reader.ReadInt32();
                                    byte[] wallet = reader.ReadBytes(walletLen);
                                    Presence p = PresenceList.getPresenceByAddress(wallet);
                                    if (p != null)
                                    {
                                        lock (p)
                                        {
                                            byte[][] presence_chunks = p.getByteChunks();
                                            foreach (byte[] presence_chunk in presence_chunks)
                                            {
                                                endpoint.sendData(ProtocolMessageCode.updatePresence, presence_chunk, null);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        // TODO blacklisting point
                                        Logging.warn(string.Format("Node has requested presence information about {0} that is not in our PL.", Base58Check.Base58CheckEncoding.EncodePlain(wallet)));
                                    }
                                }
                            }
                        }
                        break;



                    case ProtocolMessageCode.balance:
                        {
                            using (MemoryStream m = new MemoryStream(data))
                            {
                                using (BinaryReader reader = new BinaryReader(m))
                                {
                                    int address_length = reader.ReadInt32();
                                    byte[] address = reader.ReadBytes(address_length);

                                    // Retrieve the latest balance
                                    IxiNumber balance = reader.ReadString();

                                    if (address.SequenceEqual(Node.walletStorage.getPrimaryAddress()))
                                    {
                                        // Retrieve the blockheight for the balance
                                        ulong block_height = reader.ReadUInt64();

                                        if (block_height > Node.balance.blockHeight && (Node.balance.balance != balance || Node.balance.blockHeight == 0))
                                        {
                                            byte[] block_checksum = reader.ReadBytes(reader.ReadInt32());

                                            Node.balance.address = address;
                                            Node.balance.balance = balance;
                                            Node.balance.blockHeight = block_height;
                                            Node.balance.blockChecksum = block_checksum;
                                            Node.balance.verified = false;
                                        }
                                    }
                                }
                            }
                        }
                        break;

                    case ProtocolMessageCode.bye:
                        {
                            using (MemoryStream m = new MemoryStream(data))
                            {
                                using (BinaryReader reader = new BinaryReader(m))
                                {
                                    endpoint.stop();

                                    bool byeV1 = false;
                                    try
                                    {
                                        ProtocolByeCode byeCode = (ProtocolByeCode)reader.ReadInt32();
                                        string byeMessage = reader.ReadString();
                                        string byeData = reader.ReadString();

                                        byeV1 = true;

                                        switch (byeCode)
                                        {
                                            case ProtocolByeCode.bye: // all good
                                                break;

                                            case ProtocolByeCode.forked: // forked node disconnected
                                                Logging.info(string.Format("Disconnected with message: {0} {1}", byeMessage, byeData));
                                                break;

                                            case ProtocolByeCode.deprecated: // deprecated node disconnected
                                                Logging.info(string.Format("Disconnected with message: {0} {1}", byeMessage, byeData));
                                                break;

                                            case ProtocolByeCode.incorrectIp: // incorrect IP
                                                if (IxiUtils.validateIPv4(byeData))
                                                {
                                                    if (NetworkClientManager.getConnectedClients(true).Length < 2)
                                                    {
                                                        IxianHandler.publicIP = byeData;
                                                        Logging.info("Changed internal IP Address to " + byeData + ", reconnecting");
                                                    }
                                                }
                                                break;

                                            case ProtocolByeCode.notConnectable: // not connectable from the internet
                                                Logging.error("This node must be connectable from the internet, to connect to the network.");
                                                Logging.error("Please setup uPNP and/or port forwarding on your router for port " + IxianHandler.publicPort + ".");
                                                NetworkServer.connectable = false;
                                                break;

                                            case ProtocolByeCode.insufficientFunds:
                                                break;

                                            default:
                                                Logging.warn(string.Format("Disconnected with message: {0} {1}", byeMessage, byeData));
                                                break;
                                        }
                                    }
                                    catch (Exception)
                                    {

                                    }
                                    if (byeV1)
                                    {
                                        return;
                                    }

                                    reader.BaseStream.Seek(0, SeekOrigin.Begin);

                                    // Retrieve the message
                                    string message = reader.ReadString();

                                    if (message.Length > 0)
                                        Logging.info(string.Format("Disconnected with message: {0}", message));
                                    else
                                        Logging.info("Disconnected");
                                }
                            }
                        }
                        break;

                    case ProtocolMessageCode.extend:
                        {
                            if(Config.isTestClient)
                            {
                                TestClientNode.handleExtendProtocol(data);
                            }
                        }
                        break;

                    case ProtocolMessageCode.blockHeaders:
                        {
                            // Forward the block headers to the TIV handler
                            Node.tiv.receivedBlockHeaders(data, endpoint);
                        }
                        break;

                    case ProtocolMessageCode.pitData:
                        {
                            Node.tiv.receivedPIT(data, endpoint);
                        }
                        break;

                    default:
                        break;

                }
            }
            catch (Exception e)
            {
                Logging.error(string.Format("Error parsing network message. Details: {0}", e.ToString()));
            }
        }

    }
}