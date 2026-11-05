using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace CiciPDA
{
    public sealed class TunnelClient : IDisposable
    {
        readonly string _adbPath;
        readonly string _serial;
        readonly int _localPort;
        readonly int _remotePort;
        readonly byte[] _key;
        Process _adbFwd, _adbRev;
        TcpListener _listener;
        CancellationTokenSource _cts;

        public TunnelClient(string adbPath, string serial, int localPort, int remotePort, byte[] key)
        {
            _adbPath = adbPath; _serial = serial; _localPort = localPort; _remotePort = remotePort; _key = key;
        }

        public async Task StartAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            RunAdb($"forward tcp:{_localPort} tcp:{_remotePort}", out _adbFwd);
            RunAdb($"reverse tcp:{_remotePort} tcp:{_localPort}", out _adbRev);
            await Task.Delay(500, ct);

            _listener = new TcpListener(IPAddress.Loopback, _localPort);
            _listener.Start();
            _ = AcceptLoopAsync(_cts.Token);
        }

        async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); }
                catch (ObjectDisposedException) { break; }
                catch { await Task.Delay(100, ct); continue; }
                _ = HandleClientAsync(client, ct);
            }
        }

        async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var ns = client.GetStream())
            {
                try
                {
                    byte[] hello = new byte[32];
                    if (await ns.ReadExactlyAsync(hello, ct) != hello.Length) return;
                    string target = Encoding.ASCII.GetString(hello).TrimEnd('\0');
                    int ci = target.LastIndexOf(':');
                    string host = target.Substring(0, ci);
                    int port = int.Parse(target.Substring(ci + 1));

                    using var remote = new TcpClient();
                    await remote.ConnectAsync(host, port, ct);
                    remote.NoDelay = true;
                    client.NoDelay = true;

                    var keySpec = new AesGcmKey(_key);
                    using var up = new CryptoStream(ns, keySpec.CreateEncryptor(), CryptoStreamMode.Write, true);
                    using var down = new CryptoStream(remote.GetStream(), keySpec.CreateDecryptor(), CryptoStreamMode.Read, true);
                    await Task.WhenAll(
                        up.CopyToAsync(remote.GetStream(), 81920, ct),
                        down.CopyToAsync(ns, 81920, ct)
                    );
                }
                catch (OperationCanceledException) { }
                catch { }
            }
        }

        void RunAdb(string args, out Process p)
        {
            p = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = _adbPath,
                    Arguments = $"-s {_serial} {args}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            p.Start();
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _adbFwd?.Kill(); _adbRev?.Kill();
            _cts?.Dispose();
        }
    }

    public sealed class AesGcmKey : IDisposable
    {
        readonly byte[] _key;
        AesGcm _aes;
        public AesGcmKey(byte[] key) { _key = key; _aes = new AesGcm(key); }
        public ICryptoTransform CreateEncryptor() => new AesGcmTransform(_aes, true);
        public ICryptoTransform CreateDecryptor() => new AesGcmTransform(_aes, false);
        public void Dispose() { _aes?.Dispose(); }
    }

    public sealed class AesGcmTransform : ICryptoTransform
    {
        readonly AesGcm _aes;
        readonly bool _encrypt;
        long _ctr;
        public int InputBlockSize => 16384;
        public int OutputBlockSize => 16384 + 16 + 2;
        public bool CanTransformMultipleBlocks => true;
        public bool CanReuseTransform => true;

        public AesGcmTransform(AesGcm aes, bool encrypt) { _aes = aes; _encrypt = encrypt; }

        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
        {
            var nonce = new byte[12];
            long c = Interlocked.Increment(ref _ctr);
            for (int i = 0; i < 8; i++) nonce[11 - i] = (byte)(c >> (8 * i));
            nonce[3] = (byte)(_encrypt ? 1 : 0);

            var tag = new byte[16];
            if (_encrypt)
            {
                _aes.Encrypt(nonce, inputBuffer.AsSpan(inputOffset, inputCount), outputBuffer.AsSpan(outputOffset + 2), tag);
                outputBuffer[outputOffset] = (byte)(inputCount >> 8);
                outputBuffer[outputOffset + 1] = (byte)inputCount;
                Buffer.BlockCopy(tag, 0, outputBuffer, outputOffset + 2 + inputCount, 16);
                return inputCount + 18;
            }
            else
            {
                if (inputCount < 18) throw new CryptographicException();
                int len = (inputBuffer[inputOffset] << 8) | inputBuffer[inputOffset + 1];
                if (inputCount != len + 18) throw new CryptographicException();
                Buffer.BlockCopy(inputBuffer, inputOffset + 2 + len, tag, 0, 16);
                _aes.Decrypt(nonce, inputBuffer.AsSpan(inputOffset + 2, len), outputBuffer.AsSpan(outputOffset), tag);
                return len;
            }
        }

        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount) => throw new NotSupportedException();
        public void Dispose() { }
    }

    public static class StreamExtensions
    {
        public static async Task<int> ReadExactlyAsync(this Stream s, byte[] buffer, CancellationToken ct)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await s.ReadAsync(buffer.AsMemory(total), ct);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        public static async Task CopyToAsync(this Stream source, Stream dest, int bufferSize, CancellationToken ct)
        {
            var buf = new byte[bufferSize];
            int n;
            while ((n = await source.ReadAsync(buf.AsMemory(0, bufferSize), ct)) > 0)
            {
                await dest.WriteAsync(buf.AsMemory(0, n), ct);
                await dest.FlushAsync(ct);
            }
        }
    }
}