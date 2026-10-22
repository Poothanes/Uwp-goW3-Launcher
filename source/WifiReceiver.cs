using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Networking;
using Windows.Networking.Connectivity;

namespace GoW3Installer
{
    sealed class WifiReceiver : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Any, 8077);
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        readonly string destination;
        readonly Func<Task<long>> getFreeBytes;
        readonly Action<long, long, string> progress;
        readonly TaskCompletionSource<bool> completed = new TaskCompletionSource<bool>();
        WifiFolderTransfer folderTransfer;
        int transferGate;

        internal string Code { get; }
        internal string Url { get; }
        internal Task Completion => completed.Task;

        internal WifiReceiver(string destination, Func<Task<long>> getFreeBytes, Action<long, long, string> progress)
        {
            this.destination = destination;
            this.getFreeBytes = getFreeBytes;
            this.progress = progress;
            Code = GenerateCode();
            Url = "http://" + FindLocalAddress() + ":8077/";
            listener.Start();
            Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            try
            {
                while (!cancel.IsCancellationRequested && !completed.Task.IsCompleted)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var ignored = HandleClient(client);
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (!cancel.IsCancellationRequested) completed.TrySetException(ex);
            }
        }

        async Task HandleClient(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                WifiRequestHead head;
                try { head = WifiProtocol.ReadRequestHead(stream); }
                catch (Exception ex) { WriteReply(stream, "400 Bad Request", "text/plain", ex.Message); return; }

                if (head.Method == "GET" && head.Path == "/")
                {
                    string page = File.ReadAllText(Path.Combine(Package.Current.InstalledLocation.Path, "WifiTransfer.html"));
                    WriteReply(stream, "200 OK", "text/html; charset=utf-8", page);
                    return;
                }

                if (!head.Headers.TryGetValue("X-Transfer-Code", out string code) || code != Code)
                {
                    WriteReply(stream, "403 Forbidden", "text/plain", "Transfer code did not match the code shown on the Xbox.");
                    return;
                }

                if (head.Method == "POST" && head.Path == "/manifest")
                {
                    if (Interlocked.CompareExchange(ref transferGate, 1, 0) != 0)
                    {
                        WriteReply(stream, "409 Conflict", "text/plain", "A transfer is already running.");
                        return;
                    }
                    try
                    {
                        long free = await getFreeBytes();
                        folderTransfer = new WifiFolderTransfer(destination, free, progress, cancel.Token);
                        string response = await Task.Run(() =>
                        {
                            using (var body = WifiProtocol.OpenFixedLengthBody(stream, head.ContentLength))
                                return folderTransfer.Begin(body);
                        });
                        WriteReply(stream, "200 OK", "application/json; charset=utf-8", response);
                    }
                    catch (Exception ex)
                    {
                        folderTransfer?.Abort();
                        folderTransfer = null;
                        Interlocked.Exchange(ref transferGate, 0);
                        WriteReply(stream, "400 Bad Request", "text/plain", ex.Message);
                    }
                    return;
                }

                if (head.Method == "PUT" && head.Path == "/file")
                {
                    if (folderTransfer == null || !head.Query.TryGetValue("path", out string relPath))
                    {
                        WriteReply(stream, "400 Bad Request", "text/plain", "No active folder transfer or missing file path.");
                        return;
                    }
                    try
                    {
                        await Task.Run(() =>
                        {
                            using (var body = WifiProtocol.OpenFixedLengthBody(stream, head.ContentLength))
                                folderTransfer.ReceiveFile(relPath, head.ContentLength, body);
                        });
                        WriteReply(stream, "200 OK", "text/plain", "File verified.");
                    }
                    catch (Exception ex)
                    {
                        folderTransfer = null;
                        Interlocked.Exchange(ref transferGate, 0);
                        WriteReply(stream, "400 Bad Request", "text/plain", ex.Message);
                        completed.TrySetException(ex);
                    }
                    return;
                }

                if (head.Method == "POST" && head.Path == "/complete")
                {
                    if (folderTransfer == null)
                    {
                        WriteReply(stream, "400 Bad Request", "text/plain", "No active folder transfer.");
                        return;
                    }
                    try
                    {
                        folderTransfer.Complete();
                        WriteReply(stream, "200 OK", "text/plain", "Transfer complete and verified! Press Prepare game on your Xbox.");
                        completed.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        folderTransfer = null;
                        Interlocked.Exchange(ref transferGate, 0);
                        WriteReply(stream, "400 Bad Request", "text/plain", ex.Message);
                        completed.TrySetException(ex);
                    }
                    return;
                }

                WriteReply(stream, "404 Not Found", "text/plain", "Unsupported Wi-Fi transfer endpoint.");
            }
        }

        static void WriteReply(Stream stream, string status, string type, string body)
        {
            byte[] payload = Encoding.UTF8.GetBytes(body ?? "");
            byte[] header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + payload.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        static string GenerateCode()
        {
            byte[] b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            uint value = BitConverter.ToUInt32(b, 0) % 900000 + 100000;
            return value.ToString();
        }

        static string FindLocalAddress()
        {
            var host = NetworkInformation.GetHostNames()
                .FirstOrDefault(h => h.Type == HostNameType.Ipv4 && h.IPInformation?.NetworkAdapter != null && !h.CanonicalName.StartsWith("169.254."));
            return host?.CanonicalName ?? "127.0.0.1";
        }

        public void Dispose()
        {
            cancel.Cancel();
            folderTransfer?.Abort();
            try { listener.Stop(); } catch { }
            completed.TrySetCanceled();
        }
    }
}
