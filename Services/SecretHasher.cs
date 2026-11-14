using System.Security.Cryptography;

namespace NumberBomb.Services;

/// <summary>
/// 密码哈希：PBKDF2-SHA256 / 60 万次迭代 / 16 字节随机盐。
/// 存成 "pbkdf2$迭代$盐$哈希"，不存明文，也不存可逆的东西。
/// 迭代次数写在哈希串里，所以老账号（12 万次）照样能登，
/// 登进来那一刻顺手升级成新参数（见 NeedsRehash）。
/// </summary>
public sealed class SecretHasher
{
    public const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            var iterations = int.Parse(parts[1]);
            if (iterations < 1) return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>这条哈希是不是用更弱的旧参数存的？是的话登录后应该重算一遍。</summary>
    public static bool NeedsRehash(string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return true;
        return !int.TryParse(parts[1], out var iterations) || iterations < Iterations;
    }
}
