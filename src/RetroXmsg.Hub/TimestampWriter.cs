//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroXmsg - Accessing yesterday's Xmsg API with today's standards
//

using System;
using System.IO;
using System.Text;

namespace RetroXmsg.Hub
{
    /// <summary>
    /// Wraps a text writer so every line it emits starts with a wall-clock date and time.
    /// </summary>
    /// <remarks>
    /// The shape of a line is "yyyy-MM-dd HH:mm:ss.fff | message". The pipe with a space on each
    /// side is a plain, easy-to-split marker: split on the first " | " and you have the time and
    /// the message, with no guessing, even when the message itself holds a bare '|'.
    ///
    /// WHY THIS CLASS EXISTS TWICE
    ///  - The live runner (SRC/RetroXmsg.Runner/Program.cs) has the same class, and the format here
    ///    is copied from it CHARACTER FOR CHARACTER on purpose, so a hub log and a runner log can be
    ///    read side by side and lined up by time.
    ///  - It is copied rather than shared because the hub project has no project references at all.
    ///    That is a deliberate choice written down in RetroXmsg.Hub.csproj: the hub is the wire between
    ///    machines and stays free of the XMSG stack. Pulling in the whole live library just to get
    ///    forty lines of writer would throw that away. If a small shared logging package ever
    ///    appears, both copies should move into it.
    ///  - If you change the format here, change it in the runner too, or the two logs stop lining up.
    ///
    /// A note on threads: the hub writes log lines from several connection threads. Console.SetOut
    /// and Console.SetError wrap whatever they are given in a synchronised writer, so only one
    /// thread is inside Write at a time and the "am I at the start of a line" flag stays honest.
    /// </remarks>
    internal sealed class TimestampWriter : TextWriter
    {
        /// <summary>
        /// The writer that receives the finished text.
        /// </summary>
        private readonly TextWriter _inner;

        /// <summary>
        /// True when the next character written begins a new line and so needs a stamp in front.
        /// </summary>
        private bool _atLineStart = true;

        /// <summary>
        /// What separates the time from the message. Split a line on the first one of these.
        /// </summary>
        private const string Delimiter = " | ";

        /// <summary>
        /// The time format: full date, time to the millisecond. Milliseconds matter because frames
        /// arrive and are forwarded within the same second and the order is the whole point.
        /// </summary>
        private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        /// <summary>
        /// Creates a writer that stamps each line and passes it on.
        /// </summary>
        /// <param name="inner">
        /// The writer that gets the stamped text. Usually the real console.
        /// </param>
        public TimestampWriter(TextWriter inner)
        {
            _inner = inner;
        }

        /// <summary>
        /// The character encoding of the writer underneath.
        /// </summary>
        public override Encoding Encoding
        {
            get { return _inner.Encoding; }
        }

        /// <summary>
        /// Writes one character, putting the date and time in front if a new line is starting.
        /// </summary>
        /// <param name="value">
        /// The character to write.
        /// </param>
        public override void Write(char value)
        {
            if (_atLineStart)
            {
                _inner.Write(DateTime.Now.ToString(TimeFormat));
                _inner.Write(Delimiter);
                _atLineStart = false;
            }

            _inner.Write(value);

            // Only the newline itself arms the next stamp. A carriage return does not, because
            // Windows sends "\r\n" and we would otherwise stamp the empty gap between the two.
            if (value == '\n')
            {
                _atLineStart = true;
            }
        }

        /// <summary>
        /// Writes a string one character at a time so each new line inside it gets its own stamp.
        /// </summary>
        /// <param name="value">
        /// The text to write. A null is ignored, matching what a plain text writer does.
        /// </param>
        public override void Write(string? value)
        {
            if (value == null)
            {
                return;
            }

            // A plain for loop, not foreach: house style, and it avoids the enumerator entirely.
            for (int i = 0; i < value.Length; i++)
            {
                Write(value[i]);
            }
        }

        /// <summary>
        /// Pushes anything buffered underneath out to its destination.
        /// </summary>
        public override void Flush()
        {
            _inner.Flush();
        }
    }
}
