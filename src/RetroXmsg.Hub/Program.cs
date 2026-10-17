//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.Threading;

namespace RetroXmsg.Hub
{
    /// <summary>
    /// Runs a virtual Ethernet segment that RetroCore machines and observers join over TCP.
    /// </summary>
    /// <remarks>
    /// This lives in the XMSG repository, not in RetroCore, because it belongs to the COSMOS
    /// networking work and moves with it into RetroFS. It is not part of any emulated machine: a
    /// machine emulates hardware, a hub is the wire between machines, and several machines in
    /// separate processes share one hub.
    /// </remarks>
    public static class Program
    {
        /// <summary>
        /// How often the running hub prints its counters, in milliseconds.
        /// </summary>
        private const int StatusIntervalMs = 10000;

        /// <summary>
        /// Entry point.
        /// </summary>
        /// <param name="args">
        /// Command line: <c>--port N</c>, optionally <c>--uplink host:port</c> and <c>--quiet</c>.
        /// </param>
        /// <returns>
        /// Zero on a clean shutdown, non-zero on a bad command line or a hub that cannot start.
        /// </returns>
        public static int Main(string[] args)
        {
            // Put a date and time in front of EVERY line before anything is printed.
            //
            // Both streams are wrapped, not just the normal one: the bad-command-line messages and
            // the "hub failed to start" message go to the error stream, and a line without a time
            // on it is exactly the line you end up trying to place against a runner log later.
            // The usage text is stamped too. That is on purpose - one rule, no line escapes it.
            //
            // The format matches the live runner's TimestampWriter character for character, so hub
            // and runner logs can be read side by side. See TimestampWriter.cs for why the class is
            // copied instead of shared.
            Console.SetOut(new TimestampWriter(Console.Out));
            Console.SetError(new TimestampWriter(Console.Error));

            int port = 5010;
            string? uplinkHost = null;
            int uplinkPort = 0;
            bool quiet = false;
            string? capturePath = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];

                if (string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase))
                {
                    PrintUsage();
                    return 0;
                }

                if (string.Equals(a, "--quiet", StringComparison.OrdinalIgnoreCase))
                {
                    quiet = true;
                    continue;
                }

                if (string.Equals(a, "--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out port) || port < 0 || port > 65535)
                    {
                        Console.Error.WriteLine("--port must be 0-65535 (0 picks a free port).");
                        return 2;
                    }

                    continue;
                }

                if (string.Equals(a, "--capture", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    capturePath = args[++i];
                    continue;
                }

                if (string.Equals(a, "--uplink", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    string up = args[++i];
                    int colon = up.LastIndexOf(':');
                    if (colon <= 0 || !int.TryParse(up.Substring(colon + 1), out uplinkPort))
                    {
                        Console.Error.WriteLine("--uplink must be host:port");
                        return 2;
                    }

                    uplinkHost = up.Substring(0, colon);
                    continue;
                }

                Console.Error.WriteLine($"Unknown argument '{a}'.");
                PrintUsage();
                return 2;
            }

            HubServer hub = new HubServer(port, uplinkHost, uplinkPort);
            hub.Log += message => Console.WriteLine($"[hub] {message}");

            // Open the capture BEFORE Start, so the very first frame of the run is in the file.
            // A capture that begins after the machines have said hello is missing the part that
            // sets up everything that follows.
            if (capturePath != null)
            {
                try
                {
                    hub.StartCapture(capturePath);
                    Console.WriteLine($"Capturing every frame to {capturePath} (pcap, link type Ethernet).");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Cannot write capture file: {ex.GetType().Name} - {ex.Message}");
                    return 2;
                }
            }

            try
            {
                hub.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Hub failed to start: {ex.GetType().Name} - {ex.Message}");
                return 1;
            }

            Console.WriteLine($"XMSG Ethernet hub running: {hub.Description}");
            Console.WriteLine($"Machines join with:  device add ETH 0 --net=tcp:127.0.0.1:{hub.Port}");
            Console.WriteLine("Ctrl-C to stop.");

            ManualResetEventSlim stopping = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                stopping.Set();
            };

            // The last status line printed. Compared against the next one so an unchanged
            // segment stays silent instead of repeating itself. Empty means nothing has
            // been printed yet, so the first pass always prints - which is what says the
            // hub is alive and counting.
            string lastStatus = "";

            while (!stopping.IsSet)
            {
                if (stopping.Wait(StatusIntervalMs))
                {
                    break;
                }

                if (!quiet)
                {
                    string status =
                        $"[hub] members {hub.MemberCount} (machines {hub.MachineMemberCount})   " +
                        $"in {hub.FramesIn}  fwd {hub.FramesForwarded}   " +
                        $"dropped slow {hub.FramesDroppedSlow} / loop {hub.FramesDroppedLoop} / ttl {hub.FramesDroppedTtl}" +
                        (capturePath == null ? "" : $"   captured {hub.FramesCaptured}");

                    // ONLY PRINT WHEN SOMETHING MOVED. An idle segment used to write the
                    // identical line every ten seconds for ever, which buries the lines that
                    // do carry news - a member joining, a counter climbing, a frame dropped -
                    // under thousands of copies of itself. Every number the line can show is
                    // part of the compared text, so any change at all still prints.
                    if (status != lastStatus)
                    {
                        Console.WriteLine(status);
                        lastStatus = status;
                    }
                }
            }

            Console.WriteLine("Stopping...");

            // Read the count BEFORE Stop, which closes the writer and zeroes it.
            long captured = hub.FramesCaptured;
            hub.Stop();
            Console.WriteLine(
                $"Final: in {hub.FramesIn}  forwarded {hub.FramesForwarded}  " +
                $"dropped slow {hub.FramesDroppedSlow} / loop {hub.FramesDroppedLoop} / ttl {hub.FramesDroppedTtl}");
            if (capturePath != null)
            {
                Console.WriteLine($"Capture closed: {captured} frames in {capturePath}");
            }
            return 0;
        }

        /// <summary>
        /// Prints the command line.
        /// </summary>
        private static void PrintUsage()
        {
            Console.WriteLine("xmsghub - a virtual Ethernet segment for ND machines and observers");
            Console.WriteLine();
            Console.WriteLine("  xmsghub --port 5010                      run a hub on port 5010");
            Console.WriteLine("  xmsghub --port 5010 --uplink host:5010   also join a remote hub");
            Console.WriteLine("  xmsghub --port 0                         pick a free port");
            Console.WriteLine("  xmsghub --quiet                          no periodic counters");
            Console.WriteLine("  xmsghub --capture run.pcap               store every frame (pcap, Ethernet)");
            Console.WriteLine();
            Console.WriteLine("Machines join with:  device add ETH 0 --net=tcp:HOST:PORT");
            Console.WriteLine("A hub with no --uplink is the root of the tree.");
        }
    }
}
