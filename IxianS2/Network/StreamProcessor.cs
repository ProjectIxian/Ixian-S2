using IXICore;
using IXICore.Meta;
using IXICore.Network;
using System;
using System.Collections.Generic;
using System.IO;

namespace S2.Network
{
    class StreamTransaction
    {
        public string messageID;
        public Transaction transaction;
    }


    class StreamProcessor
    {
        static List<StreamMessage> messages = new List<StreamMessage>(); // List that stores stream messages
        static List<StreamTransaction> transactions = new List<StreamTransaction>(); // List that stores stream transactions

        public static ulong bytesReceived = 0; // S2 data received
        public static ulong bytesSent = 0; // S2 data sent

        // Called when receiving S2 data from clients
        public static void receiveData(byte[] bytes, RemoteEndpoint endpoint)
        {
            bytesReceived += (ulong)bytes.Length;

            string endpoint_wallet_string = endpoint.presence.wallet.ToString();
            Logging.info(string.Format("Receiving S2 data from {0}", endpoint_wallet_string));

            StreamMessage message = new StreamMessage(bytes);

            // Don't allow clients to send error stream messages, as it's reserved for S2 nodes only
            if(message.type == StreamMessageCode.error)
            {
                Logging.warn(string.Format("Discarding error message type from {0}", endpoint_wallet_string));
                return;
            }

            // TODO: commented for development purposes ONLY!
            /*if (QuotaManager.exceededQuota(endpoint.presence.wallet))
            {
                Logging.error(string.Format("Exceeded quota of info relay messages for {0}", endpoint_wallet_string));
                sendError(endpoint.presence.wallet);
                return;
            }*/

            bool data_message = false;
            if (message.type == StreamMessageCode.data)
                data_message = true;

            QuotaManager.addActivity(endpoint.presence.wallet, data_message);

            // Relay certain messages without transaction
            if(!NetworkServer.forwardMessage(message.recipient, ProtocolMessageCode.s2data, bytes))
            {
                // Couldn't forward the message, send failed to client
                sendError(message.sender, message.recipient, message.id, endpoint);
                return;
            }
            bytesSent += (ulong)bytes.Length;

            // TODO: commented for development purposes ONLY!
            /*
                        // Extract the transaction
                        Transaction transaction = new Transaction(message.transaction);

                        // Validate transaction sender
                        if(transaction.from.SequenceEqual(message.sender) == false)
                        {
                            Logging.error(string.Format("Relayed message transaction mismatch for {0}", endpoint_wallet_string));
                            sendError(message.sender);
                            return;
                        }

                        // Validate transaction amount and fee
                        if(transaction.amount < CoreConfig.relayPriceInitial || transaction.fee < CoreConfig.forceTransactionPrice)
                        {
                            Logging.error(string.Format("Relayed message transaction amount too low for {0}", endpoint_wallet_string));
                            sendError(message.sender);
                            return;
                        }

                        // Validate transaction receiver
                        if (transaction.toList.Keys.First().SequenceEqual(IxianHandler.getWalletStorage().address) == false)
                        {
                            Logging.error("Relayed message transaction receiver is not this S2 node");
                            sendError(message.sender);
                            return;
                        }

                        // Update the recipient dictionary
                        if (dataRelays.ContainsKey(message.recipient))
                        {
                            dataRelays[message.recipient]++;
                            if(dataRelays[message.recipient] > Config.relayDataMessageQuota)
                            {
                                Logging.error(string.Format("Exceeded amount of unpaid data relay messages for {0}", endpoint_wallet_string));
                                sendError(message.sender);
                                return;
                            }
                        }
                        else
                        {
                            dataRelays.Add(message.recipient, 1);
                        }


                        // Store the transaction
                        StreamTransaction streamTransaction = new StreamTransaction();
                        streamTransaction.messageID = message.getID();
                        streamTransaction.transaction = transaction;
                        lock (transactions)
                        {
                            transactions.Add(streamTransaction);
                        }

                        // For testing purposes, allow the S2 node to receive relay data itself
                        if (message.recipient.SequenceEqual(IxianHandler.getWalletStorage().getWalletAddress()))
                        {               
                            string test = Encoding.UTF8.GetString(message.data);
                            Logging.info(test);

                            return;
                        }

                        Logging.info("NET: Forwarding S2 data");
                        NetworkStreamServer.forwardMessage(message.recipient, DLT.Network.ProtocolMessageCode.s2data, bytes);      
                        */
        }

        // Called when receiving a transaction signature from a client
        public static void receivedTransactionSignature(byte[] bytes, RemoteEndpoint endpoint)
        {
            using (MemoryStream m = new MemoryStream(bytes))
            {
                using (BinaryReader reader = new BinaryReader(m))
                {
                    // Read the message ID
                    string messageID = reader.ReadString();
                    int sig_length = reader.ReadInt32();
                    if(sig_length <= 0)
                    {
                        Logging.warn("Incorrect signature length received.");
                        return;
                    }

                    // Read the signature
                    byte[] signature = reader.ReadBytes(sig_length);

                    lock (transactions)
                    {
                        // Find the transaction with a matching message id
                        StreamTransaction tx = transactions.Find(x => x.messageID.Equals(messageID, StringComparison.Ordinal));
                        if(tx == null)
                        {
                            Logging.warn("No transaction found to match signature messageID.");
                            return;
                        }
                     
                        // Compose a new transaction and apply the received signature
                        Transaction transaction = new Transaction(tx.transaction);
                        transaction.signature = signature;

                        // Verify the signed transaction
                        if (transaction.verifySignature(transaction.pubKey.pubKey, null))
                        {
                            // Broadcast the transaction
                            CoreProtocolMessage.broadcastProtocolMessage(new char[] { 'M', 'H' }, ProtocolMessageCode.transactionData2, transaction.getBytes(true, true), null, endpoint);
                        }
                        return;
                                                 
                    }
                }
            }
        }


        // Called periodically to clear the black list
        public static void update()
        {

        }

        // Sends an error stream message to a recipient
        public static void sendError(Address recipient, Address sender, byte[] data, RemoteEndpoint endpoint = null)
        {
            StreamMessage message = new StreamMessage();
            message.type = StreamMessageCode.error;
            message.recipient = recipient;
            message.sender = sender;
            message.data = data;
            message.encryptionType = StreamMessageEncryptionCode.none;

            if(endpoint != null)
            {
                endpoint.sendData(ProtocolMessageCode.s2data, message.getBytes());
            }else
            {
                NetworkServer.forwardMessage(recipient, ProtocolMessageCode.s2data, message.getBytes());
            }
        }
    }
}
