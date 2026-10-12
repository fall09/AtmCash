using System.Security.Cryptography;
using System.Text;

namespace AtmCash.Api.Infrastructure;

/// <summary>
/// Kart numarasını HMAC-SHA256 (gizli anahtarla) özetler. Düz SHA256 yetmez: 16 haneli kart numarası
/// uzayı küçüktür, anahtarsız özet brute-force ile geri çözülür. Anahtar yapılandırmadan gelir.
/// </summary>
public sealed class CardHasher(IConfiguration config)
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(
        config["Security:CardHashKey"] ?? throw new InvalidOperationException("Security:CardHashKey is not configured."));

    public string Hash(string cardNumber) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(cardNumber.Trim())));
}
