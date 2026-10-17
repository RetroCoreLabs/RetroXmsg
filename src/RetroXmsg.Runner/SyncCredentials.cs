//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.Collections.Generic;
using System.IO;
using RetroIO.Protocols.Xmsg;
using RetroIO.Protocols.Xmsg.Api;
using RetroIO.Protocols.Xmsg.Live;
namespace RetroXmsg.Runner
{
    /// <summary>
    /// The passwords the sync daemon needs to write into other users' directories, held as folded
    /// words.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the daemon needs these at all</b></para>
    /// <para>
    /// A file lands in the user named in the RESERVE request, and SINTRAN checks that user's
    /// password before letting anyone in. One daemon serving many users therefore has to know a
    /// password per user, or it can only ever write into the one it happens to be.
    /// </para>
    /// <para><b>The file</b></para>
    /// <code>
    /// # sync-credentials.txt - one line per user
    /// SYSTEM=
    /// UTILITY=
    /// SECRET=secret
    /// PROJECT=0x6D2A
    /// </code>
    /// <para>
    /// An empty value means the user has no password, which is the common case here and is NOT the
    /// same as the user being absent - an absent user is refused rather than tried blindly.
    /// </para>
    /// <para><b>A value may be given already folded</b></para>
    /// <para>
    /// <c>0x6D2A</c> is accepted as well as <c>secret</c>. SINTRAN never sees the plaintext anyway
    /// - it folds to one word by <c>acc = ROL16(acc,3) + toupper(c)</c> and sends only that - so
    /// anyone who would rather not keep a readable password on disk can store the word instead and
    /// lose nothing. The fold is not reversible, but it is also not a hash: two passwords can
    /// collide, so treat the word as a secret exactly as you would the password.
    /// </para>
    /// <para><b>Plaintext on disk</b></para>
    /// <para>
    /// This file is a password list. It is not encrypted and nothing here pretends otherwise. Keep
    /// it out of the repository and give it the same care as any other credential file.
    /// </para>
    /// </remarks>
    internal sealed class SyncCredentials
    {
        /// <summary>
        /// Folded password per user, upper-cased so lookup matches SINTRAN's own case rules.
        /// </summary>
        private readonly Dictionary<string, ushort> _words =
            new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets how many users are known.
        /// </summary>
        public int Count
        {
            get { return _words.Count; }
        }

        /// <summary>
        /// Loads a credential file, or returns an empty set when there is none.
        /// </summary>
        /// <param name="path">
        /// The file to read, or null when the caller gave none.
        /// </param>
        /// <param name="log">
        /// Where to report what was loaded and what was refused.
        /// </param>
        /// <returns>
        /// The credentials. Never null - a missing file is an empty set, not a failure, because a
        /// daemon serving only passwordless users needs no file at all.
        /// </returns>
        public static SyncCredentials Load(string? path, Action<string> log)
        {
            SyncCredentials result = new SyncCredentials();

            if (path == null || path.Length == 0)
            {
                return result;
            }

            if (!File.Exists(path))
            {
                // Say so rather than carrying on silently: a daemon that cannot find its password
                // list will refuse every user it was meant to serve, and the refusals would read
                // as an access fault on the machine.
                log("[sync] NO CREDENTIAL FILE at " + path
                    + " - only users with no password can be written to.");
                return result;
            }

            string[] lines = File.ReadAllLines(path);
            int refused = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();

                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    refused++;
                    log("[sync] credential line " + (i + 1) + " ignored - expected USER=password");
                    continue;
                }

                string user = line.Substring(0, split).Trim();
                string secret = line.Substring(split + 1).Trim();

                result._words[user] = Fold(secret);
            }

            log("[sync] credentials: " + result._words.Count + " user(s) from " + path
                + (refused > 0 ? " (" + refused + " line(s) ignored)" : string.Empty));

            return result;
        }

        /// <summary>
        /// Turns a configured value into the word the wire carries.
        /// </summary>
        /// <param name="secret">
        /// A plaintext password, an already-folded <c>0xNNNN</c> word, or an empty string.
        /// </param>
        /// <returns>
        /// The folded word, or zero when the user has no password.
        /// </returns>
        private static ushort Fold(string secret)
        {
            if (secret.Length == 0)
            {
                return 0;
            }

            if (secret.Length > 2 && secret[0] == '0' && (secret[1] == 'x' || secret[1] == 'X'))
            {
                ushort already;
                if (ushort.TryParse(
                        secret.Substring(2),
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out already))
                {
                    return already;
                }
            }

            return SintranPassword.Encode(secret);
        }

        /// <summary>
        /// Finds a user's folded password.
        /// </summary>
        /// <param name="user">
        /// The SINTRAN user.
        /// </param>
        /// <param name="word">
        /// Receives the folded password, or zero when the user is known to have none.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the user is listed at all.
        /// </returns>
        /// <remarks>
        /// The distinction matters and is why this does not simply return zero for everything: an
        /// unlisted user is one nobody has said anything about, and pushing into it blind produces
        /// WRONG PASSWORD refusals that look like a broken link. A listed user with an empty value
        /// is a deliberate statement that it has no password.
        /// </remarks>
        public bool TryGet(string user, out ushort word)
        {
            return _words.TryGetValue(user, out word);
        }
    }
}
