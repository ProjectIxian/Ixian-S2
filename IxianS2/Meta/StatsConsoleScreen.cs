using IXICore;
using IXICore.Meta;
using IXICore.Network;
using IXICore.Utils;
using S2.Network;
using System;
using System.Linq;
using System.Threading;

namespace S2.Meta
{
    public class StatsConsoleScreen
    {
        private DateTime startTime;

        private Thread thread = null;
        private bool running = false;

        private int consoleWidth = 55;
        private uint drawCycle = 0; // Keep a count of screen draw cycles as a basic method of preventing visual artifacts

        public StatsConsoleScreen()
        {
            Console.Clear();

            Console.CursorVisible = ConsoleHelpers.verboseConsoleOutput;

            // Start thread
            running = true;
            thread = new Thread(new ThreadStart(threadLoop));
            thread.Name = "Stats_Console_Thread";
            thread.Start();

            startTime = DateTime.UtcNow;
        }

        // Shutdown console thread
        public void stop()
        {
            running = false;
        }

        private void threadLoop()
        {
            while (running)
            {
                if (ConsoleHelpers.verboseConsoleOutput == false)
                {
                    // Clear the screen every 10 seconds to prevent any persisting visual artifacts
                    if (drawCycle > 5)
                    {
                        clearScreen();
                        drawCycle = 0;
                    }
                    else
                    {
                        drawScreen();
                        drawCycle++;
                    }
                }

                Thread.Sleep(2000);
            }
        }

        public void clearScreen()
        {
            //Console.BackgroundColor = ConsoleColor.DarkGreen;
            Console.Clear();
            drawScreen();
        }

        public void drawScreen()
        {
            Console.SetCursorPosition(0, 0);

            string server_version = checkForUpdate();
            bool update_avail = false;
            if (!server_version.StartsWith("("))
            {
                if (server_version.CompareTo(Config.version) > 0)
                {
                    update_avail = true;
                }
            }

            int connectionsOut = NetworkClientManager.getConnectedClients(true).Count();
            int connectionsIn = NetworkServer.getConnectedClients().Count();

            writeLine(" ██╗██╗  ██╗██╗ █████╗ ███╗   ██╗    ███████╗██████╗  ");
            writeLine(" ██║╚██╗██╔╝██║██╔══██╗████╗  ██║    ██╔════╝╚════██╗ ");
            writeLine(" ██║ ╚███╔╝ ██║███████║██╔██╗ ██║    ███████╗ █████╔╝ ");
            writeLine(" ██║ ██╔██╗ ██║██╔══██║██║╚██╗██║    ╚════██║██╔═══╝  ");
            writeLine(" ██║██╔╝ ██╗██║██║  ██║██║ ╚████║    ███████║███████╗ ");
            writeLine(" ╚═╝╚═╝  ╚═╝╚═╝╚═╝  ╚═╝╚═╝  ╚═══╝    ╚══════╝╚══════╝ ");
            writeLine(" {0}", (Config.version + " BETA ").PadLeft(53));
            writeLine(" {0}", ("http://localhost:" + Config.apiPort + "/"));
            writeLine("──────────────────────────────────────────────────────");
            if (update_avail)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                writeLine(" An update (" + server_version + ") of Ixian S2 is available");
                writeLine(" Please visit https://www.ixian.io");
                Console.ResetColor();
            }
            else
            {
                if (!NetworkServer.isConnectable() && connectionsOut == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    writeLine(" Your node isn't connectable from the internet.");
                    writeLine(" Please set-up port forwarding for port " + IxianHandler.publicPort + ".");
                    writeLine(" Make sure you can connect to: " + IxianHandler.getFullPublicAddress());
                    Console.ResetColor();
                }
                else
                {
                    writeLine(" Thank you for running an Ixian S2 node.");
                    writeLine(" For help please visit https://www.ixian.io");
                }
            }
            writeLine("──────────────────────────────────────────────────────");


            // Node status
            Console.Write(" Status:               ");

            string dltStatus = "active";


            string connectionsInStr = "-";  // Default to no inbound connections accepted
            if (NetworkServer.isRunning())
            {
                // If the server is running, show the number of inbound connections
                connectionsInStr = String.Format("{0}", connectionsIn);
            }

            if (connectionsIn + connectionsOut < 1)
                dltStatus = "connecting   ";

            if (IxianHandler.status == NodeStatus.stalled) // if node is stalled
            {
                Console.ForegroundColor = ConsoleColor.Red;
                dltStatus = "No block received for over 30 minutes";
            }

            writeLine(dltStatus);
            Console.ResetColor();

            writeLine("");

            writeLine(" Connections (I/O):    {0}", connectionsInStr + "/" + connectionsOut);
            writeLine(" Presences:            {0}", PresenceList.getTotalPresences());

            writeLine("");

            float recvMB = 0;
            if(StreamProcessor.bytesReceived > 0)
                recvMB = (StreamProcessor.bytesReceived / 1024f) / 1024f;
            float sentMB = 0;
            if (StreamProcessor.bytesSent > 0)
                sentMB = (StreamProcessor.bytesSent / 1024f) / 1024f;

            writeLine(" S2 Data Received:     {0:F2} MB", recvMB);
            writeLine(" S2 Data Sent:         {0:F2} MB", sentMB);
            
            writeLine("──────────────────────────────────────────────────────");

            TimeSpan elapsed = DateTime.UtcNow - startTime;

            writeLine(" Running for {0} days {1}h {2}m {3}s", elapsed.Days, elapsed.Hours, elapsed.Minutes, elapsed.Seconds);
            writeLine("");
            writeLine(" Press V to toggle stats. Esc key to exit.");

        }

        private void writeLine(string str, params object[] arguments)
        {
            Console.WriteLine(string.Format(str, arguments).PadRight(consoleWidth));
        }

        private string checkForUpdate()
        {
            if (!UpdateVerify.ready && !UpdateVerify.error) return "(checking)";
            if (UpdateVerify.ready)
            {
                if (UpdateVerify.error) return "(error)";
                return UpdateVerify.serverVersion;
            }
            return "(not checked)";
        }
    }
}
