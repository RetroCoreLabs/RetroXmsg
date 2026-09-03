using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.ListRouting;
using RetroIO.Protocols.Xmsg.Live.Logging;

using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner
{
    /// <summary>
    /// How a node is reached from the runner's own node (the "self" perspective of the topology file).
    /// </summary>
    /// <remarks>
    /// This maps directly onto the XSGSY connection class the runner advertises for the node. The hop
    /// count is DERIVED, never hand-entered, because only the local and directly-linked cases are truly
    /// knowable:
    ///  - <see cref="Local"/> is us (0 hops).
    ///  - <see cref="Neighbour"/> is the machine on the other end of our HDLC bridge (1 hop).
    ///  - <see cref="Via"/> is reached through one or more relay nodes; the hop count is the length of the via chain plus one.
    /// </remarks>
    internal enum NodeReach : byte
    {
        /// <summary>
        /// The node is this runner itself (<c>0</c> hops); advertised as <see cref="XroutConnectionType.Local"/>.
        /// </summary>
        Local = 0,

        /// <summary>
        /// The node is the direct HDLC link peer (<c>1</c> hop); advertised as <see cref="XroutConnectionType.Neighbour"/>.
        /// </summary>
        Neighbour = 1,

        /// <summary>
        /// The node is reached through one or more relay nodes; advertised as <see cref="XroutConnectionType.Via"/> with a derived hop count.
        /// </summary>
        Via = 2,
    }

    /// <summary>
    /// A direct TCP endpoint (an <c>nd100x --hdlc</c> bridge) that a node listens on, for the case where
    /// direct connection to that machine is allowed.
    /// </summary>
    internal sealed class TopologyEndpoint
    {
        /// <summary>
        /// Gets or sets the TCP host name or address of the bridge.
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// Gets or sets the TCP port the bridge listens on.
        /// </summary>
        public int Port { get; set; }
    }

    /// <summary>
    /// One node in the topology: its id, a human alias, how it is reached from the runner, and optionally
    /// the TCP endpoint of its HDLC bridge for a direct connection.
    /// </summary>
    internal sealed class TopologyNode
    {
        /// <summary>
        /// Gets or sets the node (system / CPU) number.
        /// </summary>
        public ushort Id { get; set; }

        /// <summary>
        /// Gets or sets the human-friendly alias (for example a connect-to name like <c>d102</c>); optional.
        /// </summary>
        public string? Alias { get; set; }

        /// <summary>
        /// Gets or sets the raw reachability keyword from the file (<c>local</c> / <c>neighbour</c> / <c>via</c>).
        /// </summary>
        public string Reach { get; set; } = "via";

        /// <summary>
        /// Gets or sets the link index advertised for a <see cref="NodeReach.Neighbour"/> node; defaults to <c>1</c>.
        /// </summary>
        public ushort Link { get; set; } = 1;

        /// <summary>
        /// Gets or sets the chain of relay node ids from the runner to this node, for a
        /// <see cref="NodeReach.Via"/> node (for example <c>[100]</c> means "reached through 100").
        /// </summary>
        public int[]? Via { get; set; }

        /// <summary>
        /// Gets or sets the direct TCP bridge endpoint for this node, when direct connection is allowed; optional.
        /// </summary>
        public TopologyEndpoint? Tcp { get; set; }

        /// <summary>
        /// Gets or sets how this node is reached at the transport layer: <c>hdlc</c> (the default,
        /// an <c>nd100x --hdlc</c> TCP bridge) or <c>ethernet</c> (a shared COSMOS segment).
        /// </summary>
        /// <remarks>
        /// This is the TRANSPORT, which is a separate question from <see cref="Reach"/>. A node can
        /// be a direct neighbour over either one, and the SINTRAN header is identical on both -
        /// verified transport-independent on 128/128 frames - so only the link differs.
        /// </remarks>
        public string Transport { get; set; } = "hdlc";

        /// <summary>
        /// Gets or sets the Ethernet segment settings used when <see cref="Transport"/> is
        /// <c>ethernet</c>; optional.
        /// </summary>
        public EthernetConfig? Ethernet { get; set; }

        /// <summary>
        /// Gets a value indicating whether this node is reached over an Ethernet segment.
        /// </summary>
        /// <returns>
        /// True when <see cref="Transport"/> names the Ethernet transport.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <see cref="Transport"/> is neither <c>hdlc</c> nor <c>ethernet</c>.
        /// </exception>
        public bool IsEthernet()
        {
            if (string.Equals(Transport, "hdlc", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(Transport, "ethernet", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Transport, "eth", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            throw new FormatException(
                $"Node {Id}: unknown transport '{Transport}' (expected 'hdlc' or 'ethernet').");
        }
        /// <summary>
        /// Parses <see cref="Reach"/> into the strongly-typed reachability class.
        /// </summary>
        /// <returns>
        /// The <see cref="NodeReach"/> named by <see cref="Reach"/>.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when <see cref="Reach"/> is not one of <c>local</c>, <c>neighbour</c> or <c>via</c>.
        /// </exception>
        public NodeReach ReachKind()
        {
            // Accept the two common spellings of "neighbour" so a US-spelling file still loads.
            if (string.Equals(Reach, "local", StringComparison.OrdinalIgnoreCase))
            {
                return NodeReach.Local;
            }

            if (string.Equals(Reach, "neighbour", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Reach, "neighbor", StringComparison.OrdinalIgnoreCase))
            {
                return NodeReach.Neighbour;
            }

            if (string.Equals(Reach, "via", StringComparison.OrdinalIgnoreCase))
            {
                return NodeReach.Via;
            }

            throw new FormatException(
                $"Node {Id}: unknown reach '{Reach}' (expected 'local', 'neighbour' or 'via').");
        }
    }

    /// <summary>
    /// How to reach a COSMOS Ethernet segment.
    /// </summary>
    /// <remarks>
    /// Mirrors the RetroCore machine's own <c>device add ETH 0 --net=</c> choices, so the runner and
    /// the emulated ND machines are pointed at the same segment by copying one setting.
    /// </remarks>
    internal sealed class EthernetConfig
    {
        /// <summary>
        /// Gets or sets the backend: <c>udp</c> (multicast, the default), <c>tcp</c> (dial a peer or
        /// hub) or <c>listen</c> (accept one peer).
        /// </summary>
        public string Net { get; set; } = "udp";

        /// <summary>
        /// Gets or sets the multicast group for <c>udp</c>. Defaults to the RetroCore group.
        /// </summary>
        /// <remarks>
        /// A C# process CAN see this traffic even when every node is on this host - the backend
        /// joins the group and enables multicast loopback. What cannot see it is Wireshark/npcap,
        /// because host-local multicast never reaches a real adapter. Those are different things;
        /// do not read "not capturable" as "not observable".
        /// </remarks>
        public string Group { get; set; } = "239.3.9.4";

        /// <summary>
        /// Gets or sets the port: the multicast port for <c>udp</c>, or the TCP port otherwise.
        /// </summary>
        public int Port { get; set; } = 3094;

        /// <summary>
        /// Gets or sets the host to dial for <c>tcp</c>, or the address to bind for <c>listen</c>.
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";
    }

    /// <summary>
    /// One TAD login account from the topology config: a username and an optional password.
    /// </summary>
    /// <remarks>
    /// An empty or missing password means the account is passwordless - login skips the password prompt
    /// after the username (for example <c>ronny</c> with no password). Usernames are case-insensitive.
    /// </remarks>
    internal sealed class TadUserConfig
    {
        /// <summary>
        /// Gets or sets the login name.
        /// </summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the password; empty or null means a passwordless account.
        /// </summary>
        public string? Password { get; set; }
    }

    /// <summary>
    /// The COSMOS file server's settings: which Windows folder <c>*FA-SERVER</c> serves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When this block is absent, or <see cref="Root"/> is blank, <b>the file server is not
    /// started at all</b> and the runner says so on one line. That is deliberate: the alternative -
    /// quietly defaulting to a folder beside the executable - would put whatever happens to be
    /// sitting next to the runner (its own logs, its state files, its DLLs) on the network to any
    /// machine that asks, without anyone having chosen to share it. Sharing files is an explicit
    /// act, so it needs an explicit setting.
    /// </para>
    /// <para>
    /// A relative path is taken relative to the executable's folder; an absolute path is used as
    /// given. The folder is created if it does not exist, so pointing at a fresh path is enough.
    /// </para>
    /// </remarks>
    internal sealed class FileServerConfig
    {
        /// <summary>
        /// Gets or sets the Windows folder to serve. Blank or null means the file server stays off.
        /// </summary>
        public string? Root { get; set; }

        /// <summary>
        /// Gets or sets whether the file server runs at all; defaults to <c>true</c> so setting
        /// <see cref="Root"/> is enough to turn it on.
        /// </summary>
        /// <remarks>
        /// Set this to <c>false</c> to keep a configured folder in the file while temporarily
        /// switching the server off, rather than having to delete the setting.
        /// </remarks>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the user index stamped into the synthesised directory entries.
        /// </summary>
        /// <remarks>
        /// There is no real SINTRAN user directory behind a Windows folder, so this is simply the
        /// user we present the files as owned by. Whether any client reads the field is UNKNOWN.
        /// </remarks>
        public byte UserIndex { get; set; }
    }

    /// <summary>
    /// The runner's log configuration: whether to write a file log, at what level, where, and how it is
    /// rotated (Linux-syslog numbering).
    /// </summary>
    /// <remarks>
    /// Rotation happens once on startup (a restart immediately renames the previous log and starts empty)
    /// and again whenever the live file exceeds <see cref="MaxSizeMb"/>. <see cref="Keep"/> archived
    /// versions are retained (<c>file.1</c>..<c>file.Keep</c>); older ones are deleted. Defaults match the
    /// user requirement: 2 MB per file, 3 archived versions.
    /// </remarks>
    internal sealed class LogConfig
    {
        /// <summary>
        /// Gets or sets whether file logging is enabled; defaults to <c>true</c>.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Gets or sets the verbosity level keyword (<c>off</c> / <c>error</c> / <c>warn</c> / <c>info</c> / <c>debug</c>); defaults to <c>info</c>.
        /// </summary>
        public string Level { get; set; } = "info";

        /// <summary>
        /// Gets or sets the log file name or path; a relative name is placed next to the executable. Defaults to <c>xmsg-runner.log</c>.
        /// </summary>
        public string File { get; set; } = "xmsg-runner.log";

        /// <summary>
        /// Gets or sets the size, in megabytes, at which the live log is rotated; defaults to <c>2</c>.
        /// </summary>
        public double MaxSizeMb { get; set; } = 2.0;

        /// <summary>
        /// Gets or sets the number of archived versions to keep before the oldest is deleted; defaults to <c>3</c>.
        /// </summary>
        public int Keep { get; set; } = 3;

        /// <summary>
        /// Gets the rotation threshold in bytes, derived from <see cref="MaxSizeMb"/>.
        /// </summary>
        public long MaxBytes
        {
            get { return (long)(MaxSizeMb * 1024.0 * 1024.0); }
        }

        /// <summary>
        /// Parses <see cref="Level"/> into the strongly-typed <see cref="LogLevel"/>.
        /// </summary>
        /// <returns>
        /// The parsed level; <see cref="LogLevel.Info"/> when the keyword is unrecognised (logging is
        /// non-critical, so an unknown level falls back rather than aborting the run).
        /// </returns>
        public LogLevel LevelKind()
        {
            if (Enum.TryParse(Level, true, out LogLevel parsed))
            {
                return parsed;
            }

            return LogLevel.Info;
        }
    }

    /// <summary>
    /// The runner's network topology, loaded from a JSON file: the runner's own node and every known node
    /// with its alias, reachability and optional direct-TCP endpoint.
    /// </summary>
    /// <remarks>
    /// The file is written from the runner's own ("self") perspective - each node states how it is reached
    /// FROM us. This is what feeds the XSGSY routing table, so it must be self-consistent: our own node is
    /// always local, and we never advertise a route to ourselves via another machine (that is the loop the
    /// old hardcoded table produced when run as node 102).
    /// </remarks>
    internal sealed class TopologyConfig
    {
        /// <summary>
        /// Gets or sets the runner's own node (CPU) number - the id we present on the wire by default.
        /// </summary>
        public ushort Self { get; set; }

        /// <summary>
        /// Gets or sets the known nodes.
        /// </summary>
        public List<TopologyNode> Nodes { get; set; } = new List<TopologyNode>();

        /// <summary>
        /// Gets or sets the MOTD banner line shown on the TAD login greeting (the line that replaces the
        /// stock <c>SINTRAN III - VSX/500</c>); optional.
        /// </summary>
        /// <remarks>
        /// When null or empty the TAD server falls back to its built-in
        /// <c>Emulated TAD server version vN.N.N</c> banner (the assembly version). The date/time line and
        /// the <c>--- HOST ID:nnn ---</c> line are always generated - only this middle line is configurable.
        /// </remarks>
        public string? Motd { get; set; }

        /// <summary>
        /// Gets or sets the TAD login accounts. When null or empty the TAD server falls back to a single
        /// <c>SYSTEM</c>/<c>SYSTEM</c> account.
        /// </summary>
        public List<TadUserConfig> TadUsers { get; set; } = new List<TadUserConfig>();

        /// <summary>
        /// Gets or sets the log configuration.
        /// </summary>
        public LogConfig Log { get; set; } = new LogConfig();

        /// <summary>
        /// Gets or sets the COSMOS file server settings. When null (or with a blank root) the file
        /// server is not started - see <see cref="FileServerConfig"/> for why nothing is served by
        /// default.
        /// </summary>
        public FileServerConfig? FileServer { get; set; }

        /// <summary>
        /// Gets or sets the TCP endpoint this runner LISTENS on for inbound neighbour connections, when
        /// present; <c>null</c> means the runner only dials out (the current single-link behaviour).
        /// </summary>
        /// <remarks>
        /// Reserved for the planned HUB mode where neighbours connect to us instead of us dialling them,
        /// and many machines share one runner. The accept loop and multi-link forwarding are not yet
        /// implemented; this field only records the intended listen endpoint.
        /// </remarks>
        public TopologyEndpoint? Listen { get; set; }

        // JSON is authored by hand, so tolerate comments and trailing commas and ignore property casing.
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Loads and validates a topology from a JSON file.
        /// </summary>
        /// <param name="path">
        /// The full path to the topology JSON file.
        /// </param>
        /// <returns>
        /// The parsed, validated topology.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when the file does not deserialize to a config or fails validation.
        /// </exception>
        public static TopologyConfig Load(string path)
        {
            string json = File.ReadAllText(path);
            TopologyConfig? config = JsonSerializer.Deserialize<TopologyConfig>(json, Options);
            if (config == null)
            {
                throw new FormatException($"Topology file '{path}' did not deserialize to a config.");
            }

            config.Validate(path);
            return config;
        }

        /// <summary>
        /// Validates that every node parses and that each <see cref="NodeReach.Via"/> node has a via chain.
        /// </summary>
        /// <param name="path">
        /// The source path, used only for error messages.
        /// </param>
        /// <exception cref="FormatException">
        /// Thrown when there are no nodes, a node has an unknown reach, or a via node has an empty chain.
        /// </exception>
        private void Validate(string path)
        {
            if (Nodes == null || Nodes.Count == 0)
            {
                throw new FormatException($"Topology '{path}' declares no nodes.");
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                TopologyNode node = Nodes[i];
                NodeReach reach = node.ReachKind(); // throws FormatException on an unknown keyword
                if (reach == NodeReach.Via && (node.Via == null || node.Via.Length == 0))
                {
                    throw new FormatException(
                        $"Topology '{path}': node {node.Id} is 'via' but lists no relay chain.");
                }
            }
        }

        /// <summary>
        /// Finds a node by numeric id or by alias (case-insensitive).
        /// </summary>
        /// <param name="idOrAlias">
        /// A decimal node id or an alias string.
        /// </param>
        /// <returns>
        /// The matching node, or <c>null</c> when none matches.
        /// </returns>
        public TopologyNode? Find(string idOrAlias)
        {
            if (string.IsNullOrWhiteSpace(idOrAlias))
            {
                return null;
            }

            if (int.TryParse(idOrAlias, out int id))
            {
                TopologyNode? byId = FindById(id);
                if (byId != null)
                {
                    return byId;
                }
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (string.Equals(Nodes[i].Alias, idOrAlias, StringComparison.OrdinalIgnoreCase))
                {
                    return Nodes[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Finds a node by its numeric id.
        /// </summary>
        /// <param name="id">
        /// The node id to look up.
        /// </param>
        /// <returns>
        /// The matching node, or <c>null</c> when none matches.
        /// </returns>
        public TopologyNode? FindById(int id)
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Id == id)
                {
                    return Nodes[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the id of the neighbour node (the machine on the other end of our HDLC bridge), if any.
        /// </summary>
        /// <returns>
        /// The neighbour's node id, or <c>null</c> when no node is declared as a neighbour.
        /// </returns>
        public ushort? NeighbourId()
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].ReachKind() == NodeReach.Neighbour)
                {
                    return Nodes[i].Id;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets every node declared as a direct neighbour.
        /// </summary>
        /// <returns>
        /// The neighbours, in file order. Empty when none is declared.
        /// </returns>
        /// <remarks>
        /// A relaying runner needs one link, and so one host, per neighbour -
        /// <see cref="PrimaryEthernetPeer"/> deliberately returns only the first because a lone
        /// <c>XmsgNodeHost</c> speaks over one link.
        /// </remarks>
        public List<TopologyNode> Neighbours()
        {
            List<TopologyNode> found = new List<TopologyNode>();

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].ReachKind() == NodeReach.Neighbour)
                {
                    found.Add(Nodes[i]);
                }
            }

            return found;
        }

        /// <summary>
        /// Gets the node numbers reachable THROUGH a given neighbour.
        /// </summary>
        /// <param name="neighbourId">
        /// The neighbour's node id.
        /// </param>
        /// <returns>
        /// The ids of every <c>via</c> node whose chain begins at <paramref name="neighbourId"/>.
        /// Empty when nothing is routed that way.
        /// </returns>
        /// <remarks>
        /// <para>
        /// This is the routing table a relay needs, read straight off the topology: a node declared
        /// <c>"reach": "via", "via": [ 100 ]</c> is reached by sending to the link whose peer is 100.
        /// </para>
        /// <para><b>Only the FIRST hop matters here</b></para>
        /// The chain may be longer - <c>via: [100, 42]</c> means "through 100, then through 42" -
        /// but this node's decision is only which of ITS links to use, and that is settled by the
        /// first entry. The rest is the next relay's problem, which is exactly how the captured
        /// route-through behaves: each hop re-marks the datagram and passes the original endpoints
        /// along untouched.
        /// </remarks>
        public List<ushort> NodesReachableThrough(ushort neighbourId)
        {
            List<ushort> reachable = new List<ushort>();

            for (int i = 0; i < Nodes.Count; i++)
            {
                TopologyNode candidate = Nodes[i];

                if (candidate.ReachKind() != NodeReach.Via)
                {
                    continue;
                }

                int[]? via = candidate.Via;
                if (via == null || via.Length == 0)
                {
                    continue;
                }

                if (via[0] == neighbourId)
                {
                    reachable.Add(candidate.Id);
                }
            }

            return reachable;
        }

        /// <summary>
        /// Gets the first neighbour reached over an Ethernet segment, if any.
        /// </summary>
        /// <returns>
        /// The Ethernet neighbour, or <c>null</c> when every neighbour is on HDLC.
        /// </returns>
        /// <remarks>
        /// Only the FIRST such neighbour is returned. One <c>XmsgNodeHost</c> speaks over one link,
        /// so a runner on a segment carrying several ND machines currently talks to one of them.
        /// Serving several peers at once needs a host per link and is not done yet - it is NOT
        /// simply a matter of listening wider, because each peer is a separate link with its own
        /// learned link id and its own datagram sequence.
        /// </remarks>
        public TopologyNode? PrimaryEthernetPeer()
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                TopologyNode candidate = Nodes[i];
                if (candidate.Id != Self && candidate.IsEthernet()
                    && candidate.ReachKind() == NodeReach.Neighbour)
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds the backend spec string for a node's Ethernet settings.
        /// </summary>
        /// <param name="config">
        /// The Ethernet settings, or null to use the defaults (UDP multicast on the RetroCore group).
        /// </param>
        /// <returns>
        /// A spec accepted by <c>EthernetBackendFactory.FromSpec</c>.
        /// </returns>
        /// <exception cref="FormatException">
        /// Thrown when the configured backend name is not recognised.
        /// </exception>
        public static string EthernetSpec(EthernetConfig? config)
        {
            EthernetConfig c = config ?? new EthernetConfig();

            if (string.Equals(c.Net, "udp", StringComparison.OrdinalIgnoreCase))
            {
                return $"udp:{c.Group}:{c.Port}";
            }

            if (string.Equals(c.Net, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                return $"tcp:{c.Host}:{c.Port}";
            }

            if (string.Equals(c.Net, "listen", StringComparison.OrdinalIgnoreCase))
            {
                return $"listen:{c.Port}";
            }

            throw new FormatException(
                $"Unknown ethernet net '{c.Net}' (expected 'udp', 'tcp' or 'listen').");
        }

        /// <summary>
        /// Gets the TCP endpoint the runner should connect to for its HDLC link.
        /// </summary>
        /// <returns>
        /// The neighbour's TCP endpoint when present; otherwise the first node's endpoint that has one;
        /// otherwise <c>null</c>.
        /// </returns>
        /// <remarks>
        /// The runner brings up exactly one HDLC bridge, to its neighbour, so the neighbour's endpoint is
        /// preferred. Nodes reached "via" carry their own endpoint only for a future direct-connect mode.
        /// <para>
        /// These remarks used to sit above <see cref="PrimaryEthernetPeer"/>, stranded there when the
        /// method moved - so that method advertised itself as returning a TCP endpoint. Restored 2026-08-07.
        /// </para>
        /// </remarks>
        public TopologyEndpoint? PrimaryEndpoint()
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].ReachKind() == NodeReach.Neighbour && Nodes[i].Tcp != null)
                {
                    return Nodes[i].Tcp;
                }
            }

            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Tcp != null)
                {
                    return Nodes[i].Tcp;
                }
            }

            return null;
        }

        /// <summary>
        /// Builds the XSGSY routing table relative to the effective self node.
        /// </summary>
        /// <param name="self">
        /// The node id the runner is actually presenting on the wire (may override <see cref="Self"/>).
        /// </param>
        /// <returns>
        /// The routing-table entries, self-consistent for <paramref name="self"/>.
        /// </returns>
        /// <remarks>
        /// The node matching <paramref name="self"/> is ALWAYS emitted as
        /// <see cref="XroutConnectionType.Local"/> (0 hops), regardless of what the file says, so overriding
        /// self on the command line can never produce a route to ourselves via another machine. Hop counts
        /// are derived: local = 0, neighbour = 1, via = relay-chain length + 1. The via next-hop (the XSGSY
        /// "extra info" relay system) is the first entry in the via chain.
        /// </remarks>
        public List<RoutingTableEntry> BuildRoutingEntries(ushort self)
        {
            List<RoutingTableEntry> entries = new List<RoutingTableEntry>(Nodes.Count);
            for (int i = 0; i < Nodes.Count; i++)
            {
                TopologyNode node = Nodes[i];

                // Our own node is always directly local - this is the entry that stops 100 routing to us
                // via 100 (the *->102->100->102... loop the fixed table produced when run as node 102).
                if (node.Id == self)
                {
                    entries.Add(new RoutingTableEntry(node.Id, XroutConnectionType.Local, node.Id, 0, 0));
                    continue;
                }

                NodeReach reach = node.ReachKind();
                if (reach == NodeReach.Local)
                {
                    // A 'local' node that is not us: still 0 hops as documented.
                    entries.Add(new RoutingTableEntry(node.Id, XroutConnectionType.Local, node.Id, 0, 0));
                }
                else if (reach == NodeReach.Neighbour)
                {
                    // Neighbour: 1 hop, extra info = the link index.
                    entries.Add(new RoutingTableEntry(node.Id, XroutConnectionType.Neighbour, node.Link, 1, 0));
                }
                else
                {
                    // Via: next hop = first relay in the chain, hops = relays + 1 (DERIVED, never hand-entered).
                    int[] via = node.Via ?? Array.Empty<int>();
                    ushort nextHop = via.Length > 0 ? (ushort)via[0] : (ushort)0;
                    byte hops = (byte)(via.Length + 1);
                    entries.Add(new RoutingTableEntry(node.Id, XroutConnectionType.Via, nextHop, hops, 0));
                }
            }

            return entries;
        }
    }
}
