namespace Guanfu.Application.Services;

/// <summary>敏感配置（数据库密码）的加解密。</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}
