using IXICore;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using S2.Meta;
using System;
using System.IO;
using System.Linq;

namespace S2.Network
{
    public class ProtocolMessage
    {        
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
                        handleHello(data, endpoint);
                        break;

                    case ProtocolMessageCode.helloData:
                        handleHelloData(data, endpoint);
                        break;

                    case ProtocolMessageCode.s2data:
                        StreamProcessor.receiveData(data, endpoint);
                        break;

                    case ProtocolMessageCode.s2failed:
                        Logging.error("Failed to send s2 data");
                        break;

                    case ProtocolMessageCode.s2signature:
                        StreamProcessor.receivedTransactionSignature(data, endpoint);
                        break;

                    case ProtocolMessageCode.newTransaction:
                    case ProtocolMessageCode.transactionData:
                        {
                            Transaction tx = new Transaction(data, true);

                            if (endpoint.presenceAddress.type == 'M' || endpoint.presenceAddress.type == 'H')
                            {
                                PendingTransactions.increaseReceivedCount(tx.id, endpoint.presence.wallet);
                            }

                            Node.tiv.receivedNewTransaction(tx);
                            Logging.info("Received new transaction {0}", tx.id);

                            Node.addTransactionToActivityStorage(tx);
                        }
                        break;

                    case ProtocolMessageCode.updatePresence:
                        // Parse the data and update entries in the presence list
                        PresenceList.updateFromBytes(data);
                        break;

                    case ProtocolMessageCode.keepAlivePresence:
                        byte[] address = null;
                        long last_seen = 0;
                        byte[] device_id = null;
                        bool updated = PresenceList.receiveKeepAlive(data, out address, out last_seen, out device_id, endpoint);
                        break;

                    case ProtocolMessageCode.getPresence:
                        handleGetPresence(data, endpoint);
                        break;

                    case ProtocolMessageCode.balance:
                        handleBalance(data, endpoint);
                        break;

                    case ProtocolMessageCode.bye:
                        CoreProtocolMessage.processBye(data, endpoint);
                        break;

                    case ProtocolMessageCode.blockHeaders:
                        // Forward the block headers to the TIV handler
                        Node.tiv.receivedBlockHeaders(data, endpoint);
                        break;

                    case ProtocolMessageCode.blockHeaders2:
                        // Forward the block headers to the TIV handler
                        Node.tiv.receivedBlockHeaders2(data, endpoint);
                        break;

                    case ProtocolMessageCode.pitData:
                        Node.tiv.receivedPIT(data, endpoint);
                        break;

                    case ProtocolMessageCode.pitData2:
                        Node.tiv.receivedPIT2(data, endpoint);
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

        static void handleHello(byte[] data, RemoteEndpoint endpoint)
        {
            using (MemoryStream m = new MemoryStream(data))
            {
                using (BinaryReader reader = new BinaryReader(m))
                {
                    if (data[0] == 5)
                    {
                        CoreProtocolMessage.processHelloMessageV5(endpoint, reader);
                    }
                    else
                    {
                        CoreProtocolMessage.processHelloMessageV6(endpoint, reader);
                    }
                }
            }
        }

        static void handleHelloData(byte[] data, RemoteEndpoint endpoint)
        {
            using (MemoryStream m = new MemoryStream(data))
            {
                using (BinaryReader reader = new BinaryReader(m))
                {
                    if (data[0] == 5)
                    {
                        if (CoreProtocolMessage.processHelloMessageV5(endpoint, reader))
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

                            try
                            {
                                string public_ip = reader.ReadString();
                                ((NetworkClient)endpoint).myAddress = public_ip;
                            }
                            catch (Exception)
                            {

                            }

                            string address = NetworkClientManager.getMyAddress();
                            if (address != null)
                            {
                                if (IxianHandler.publicIP != address)
                                {
                                    Logging.info("Setting public IP to " + address);
                                    IxianHandler.publicIP = address;
                                }
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
                    else
                    {
                        if (CoreProtocolMessage.processHelloMessageV6(endpoint, reader))
                        {
                            char node_type = endpoint.presenceAddress.type;
                            if (node_type != 'M' && node_type != 'H')
                            {
                                CoreProtocolMessage.sendBye(endpoint, ProtocolByeCode.expectingMaster, string.Format("Expecting master node."), "", true);
                                return;
                            }

                            ulong last_block_num = reader.ReadIxiVarUInt();

                            int bcLen = (int)reader.ReadIxiVarUInt();
                            byte[] block_checksum = reader.ReadBytes(bcLen);

                            endpoint.blockHeight = last_block_num;

                            int block_version = (int)reader.ReadIxiVarUInt();

                            try
                            {
                                string public_ip = reader.ReadString();
                                ((NetworkClient)endpoint).myAddress = public_ip;
                            }
                            catch (Exception)
                            {

                            }

                            string address = NetworkClientManager.getMyAddress();
                            if (address != null)
                            {
                                if (IxianHandler.publicIP != address)
                                {
                                    Logging.info("Setting public IP to " + address);
                                    IxianHandler.publicIP = address;
                                }
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
            }
        }

        static void handleGetPresence(byte[] data, RemoteEndpoint endpoint)
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

        static void handleBalance(byte[] data, RemoteEndpoint endpoint)
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
                            Node.balance.lastUpdate = Clock.getTimestamp();
                            Node.balance.verified = false;
                        }
                    }
                }
            }
        }
    }
}