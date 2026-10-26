using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HexFront.Tools.Common;

/// <summary>
/// 网络下载工具：带退避重试的整文件下载、Range 随机读（供远程 COG 概览读取用）。
/// 注：原始 Python 工具因本机 GDAL/schannel 证书链问题改用标准库 urllib；
/// 这里统一用 HttpClient，并对 S3 公开桶走 TLS（无敏感数据）。
/// </summary>
public static class Downloader
{
    private static readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "hexfront-tools/1.0" } },
    };

    public static async Task<byte[]> GetBytesAsync(string url, int retries = 4, CancellationToken ct = default)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                using var resp = await _client.GetAsync(url, ct);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return Array.Empty<byte>(); // 服务端无此瓦片
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < retries)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(1500 * (attempt + 1)), ct);
            }
        }
        throw new IOException($"下载失败（重试耗尽）: {url} — {last?.Message}", last);
    }

    /// <summary>下载到本地；已存在且大小一致则跳过。</summary>
    public static async Task<long> DownloadFileAsync(string url, string dest, int retries = 4,
        CancellationToken ct = default)
    {
        // 探测远端大小
        long remote = -1;
        try
        {
            using var head = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            if (head.Content.Headers.ContentLength is { } len) remote = len;
        }
        catch { /* 忽略，走普通 GET */ }

        if (File.Exists(dest) && remote > 0 && new FileInfo(dest).Length == remote)
        {
            Log.Info($"[跳过] 已在本地 {Path.GetFileName(dest)} ({remote / 1048576.0:F2} MB)");
            return remote;
        }

        var dir = Path.GetDirectoryName(dest);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
        var tmp = dest + ".part";
        Exception? last = null;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var bytes = await GetBytesAsync(url, retries: 1, ct);
                await File.WriteAllBytesAsync(tmp, bytes, ct);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
                Log.Info($"[下载] {Path.GetFileName(dest)} {bytes.Length / 1048576.0:F2} MB");
                return bytes.Length;
            }
            catch (Exception ex) when (attempt < retries)
            {
                last = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(1500 * (attempt + 1)), ct);
            }
        }
        throw new IOException($"下载失败: {url} — {last?.Message}", last);
    }

    /// <summary>HTTPS 随机读文件：通过 Range 请求按需读取，适合远程 COG 概览读取。</summary>
    public sealed class HttpRangeFile : IDisposable
    {
        private readonly string _url;
        private readonly int _chunk;
        private long _size;
        private long _cacheOff = -1;
        private byte[] _cache = Array.Empty<byte>();

        public HttpRangeFile(string url, int chunk = 1 << 20)
        {
            _url = url;
            _chunk = chunk;
            var req = new HttpRequestMessage(HttpMethod.Get, url)
            {
                Headers = { Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0) },
            };
            using var resp = _client.Send(req);
            var rng = resp.Content.Headers.ContentRange;
            _size = rng?.Length ?? resp.Content.Headers.ContentLength ?? 0;
            if (_size <= 0) throw new IOException("服务器不支持 Range 请求: " + url);
        }

        public long Length => _size;

        public byte[] Read(long offset, int length)
        {
            if (offset < 0 || offset >= _size) return Array.Empty<byte>();
            length = (int)Math.Min(length, _size - offset);
            if (_cacheOff < 0 || offset < _cacheOff || offset >= _cacheOff + _cache.Length)
            {
                var end = Math.Min(_size - 1, offset + _chunk - 1);
                var req = new HttpRequestMessage(HttpMethod.Get, _url)
                {
                    Headers = { Range = new System.Net.Http.Headers.RangeHeaderValue(offset, end) },
                };
                using var resp = _client.Send(req);
                resp.EnsureSuccessStatusCode();
                _cache = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                _cacheOff = offset;
            }
            var start = (int)(offset - _cacheOff);
            var outBuf = new byte[length];
            Array.Copy(_cache, start, outBuf, 0, length);
            return outBuf;
        }

        public void Dispose() { }
    }
}
