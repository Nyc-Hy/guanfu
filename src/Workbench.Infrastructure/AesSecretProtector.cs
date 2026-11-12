using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Workbench.Application.Services;

namespace Workbench.Infrastructure;

/// <summary>
/// AES-GCM 对称加密。密钥文件放在 WORKBENCH_HOME 下（0600），换机时随目录一起搬。
/// 不引入额外依赖，也不依赖 Windows 的 DPAPI。
/// </summary>
public sealed class AesSecretProtector : ISecretProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[] _key;

    public AesSecretProtector(IOptions<WorkbenchOptions> options)
    {
        var path = options.Value.KeyFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            _key = File.ReadAllBytes(path);
            if (_key.Length != KeySize)
            {
                throw new InvalidOperationException($"密钥文件长度异常: {path}");
            }

            return;
        }

        _key = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllBytes(path, _key);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
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
