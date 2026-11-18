using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Guanfu.Application.Services;

namespace Guanfu.Infrastructure;

/// <summary>
/// AES-GCM 对称加密。密钥文件放在数据目录 下（0600），换机时随目录一起搬。
/// 不引入额外依赖，也不依赖 Windows 的 DPAPI。
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;

    public AesSecretProtector(IOptions<GuanfuOptions> options)
    {
        var path = options.Value.KeyFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _key = LoadOrCreateKey(path);
    }

    /// <summary>
    /// 读密钥文件；不存在就原子创建一份。
    /// <para>
    /// 用 FileMode.CreateNew 是为了解决「两个进程同时首启」：两个都判定文件不存在，
    /// 后写的覆盖先写的 → 先启动进程手里的密钥和磁盘上的不一致 → 已加密的密码再也解不开。
    /// 现在只有一个进程能创建成功，另一个回读磁盘上那份，两边密钥必然一致。
    /// </para>
    /// <para>
    /// 用 UnixCreateMode 是为了解决权限窗口：先 WriteAllBytes 再 chmod 的做法，
    /// 中间那段时间文件是 0644，同机其他用户能读到密钥。现在创建那一刻就是 0600。
    /// </para>
    /// </summary>
    private static byte[] LoadOrCreateKey(string path)
    {
        if (File.Exists(path))
        {
            return ReadKey(path);
        }

        var key = RandomNumberGenerator.GetBytes(KeySize);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };

        // 文件是我们自己创建成功的标志：用来区分「自己写挂了」和「别人抢先建好了」
        var created = false;

        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (var file = new FileStream(path, options))
            {
                created = true;
                file.Write(key);
                file.Flush(flushToDisk: true);
            }
        }
        catch (IOException) when (created)
        {
            // 自己写了一半就失败：把这份半截文件删掉，
            // 否则下次启动会被这份坏密钥卡死（长度永远不对）。
            TryDelete(path);
            throw;
        }
        catch (IOException ex) when (File.Exists(path))
        {
            // CreateNew 失败 = 文件已经存在 = 别的进程抢先建好了。
            // 必须用磁盘上那份，不能用内存里这份，否则两个进程密钥不一致。
            return ReadKey(path, ex);
        }

        if (!OperatingSystem.IsWindows())
        {
            // open(2) 的 mode 还会被 umask 掩一次，极端 umask 下 0600 可能被削，
            // 这里再明确设一次（文件已建好，不会再有可见性窗口）。
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        // 回读校验：写进去的必须是一份完整的密钥
        var written = ReadKey(path);
        if (!written.AsSpan().SequenceEqual(key))
        {
            throw new InvalidOperationException($"密钥文件写入校验失败: {path}");
        }

        return key;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// 读密钥文件。另一个进程可能正在写——文件要么被锁住，要么只写了一半——重试几次再放弃。
    /// </summary>
    private static byte[] ReadKey(string path, Exception? cause = null)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length == KeySize)
                    {
                        return bytes;
                    }
                }
            }
            catch (IOException)
            {
                // 另一个进程正开着这个文件写，等它写完
            }
            catch (UnauthorizedAccessException)
            {
            }

            Thread.Sleep(25);
        }

        throw new InvalidOperationException($"密钥文件长度异常: {path}", cause);
    }

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var cipher = new byte[plainBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }

    public string Unprotect(string ciphertext)
    {
        var raw = Convert.FromBase64String(ciphertext);
        if (raw.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("密文格式不正确。");
        }

        var nonce = raw[..NonceSize];
        var tag = raw[NonceSize..(NonceSize + TagSize)];
        var cipher = raw[(NonceSize + TagSize)..];
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
