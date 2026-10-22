using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace GoW3Installer
{
    public sealed class WifiRequestHead
    {
        public string Method { get; set; }
        public string RawTarget { get; set; }
        public string Path { get; set; }
        public IDictionary<string, string> Query { get; set; }
        public IDictionary<string, string> Headers { get; set; }
        public long ContentLength { get; set; }
    }

    public static class WifiProtocol
    {
        const int MaxHeaderBytes = 64 * 1024;

        public static WifiRequestHead ReadRequestHead(Stream stream)
        {
            var bytes = new List<byte>(1024);
            int matched = 0;
            byte[] end = Encoding.ASCII.GetBytes("\r\n\r\n");
            while (bytes.Count < MaxHeaderBytes)
            {
                int next = stream.ReadByte();
                if (next < 0) throw new InvalidDataException("Incomplete HTTP request header.");
                byte b = (byte)next;
                bytes.Add(b);
                matched = b == end[matched] ? matched + 1 : (b == end[0] ? 1 : 0);
                if (matched == 4) return ParseRequestHead(Encoding.ASCII.GetString(bytes.ToArray(), 0, bytes.Count - 4));
            }
            throw new InvalidDataException("HTTP request header is too large.");
        }

        public static WifiRequestHead ParseRequestHead(string text)
        {
            string[] lines = (text ?? "").Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0]))
                throw new InvalidDataException("Missing HTTP request line.");
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported HTTP request line.");
            if (first[0] != "GET" && first[0] != "POST" && first[0] != "PUT")
                throw new InvalidDataException("Unsupported HTTP method: " + first[0]);
            if (!first[1].StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid HTTP request target.");

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("Invalid HTTP header.");
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (headers.TryGetValue("Transfer-Encoding", out string transfer))
                throw new InvalidDataException("Unsupported Transfer-Encoding: " + transfer);

            long length = 0;
            if (headers.TryGetValue("Content-Length", out string rawLength) &&
                (!long.TryParse(rawLength, out length) || length < 0))
                throw new InvalidDataException("Invalid Content-Length.");

            string path = first[1];
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int q = first[1].IndexOf('?');
            if (q >= 0)
            {
                path = first[1].Substring(0, q);
                foreach (string part in first[1].Substring(q + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int eq = part.IndexOf('=');
                    string key = eq >= 0 ? part.Substring(0, eq) : part;
                    string value = eq >= 0 ? part.Substring(eq + 1) : "";
                    query[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
                }
            }

            return new WifiRequestHead
            {
                Method = first[0],
                RawTarget = first[1],
                Path = path,
                Query = query,
                Headers = headers,
                ContentLength = length
            };
        }

        public static Stream OpenFixedLengthBody(Stream stream, long length)
        {
            return new FixedLengthReadStream(stream, length);
        }

        sealed class FixedLengthReadStream : Stream
        {
            readonly Stream inner;
            long remaining;
            internal FixedLengthReadStream(Stream inner, long length) { this.inner = inner; remaining = length; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining <= 0) return 0;
                int ask = (int)Math.Min(count, remaining);
                int read = inner.Read(buffer, offset, ask);
                if (read <= 0) throw new InvalidDataException("Unexpected end of HTTP request body.");
                remaining -= read;
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
