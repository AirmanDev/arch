using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirAdmin.Recovery;

public sealed class RecoveryCryptoService : IDisposable
{
    private readonly RSA _rsa = RSA.Create(3072);
    private readonly object _gate = new();
    private string? _challenge;
    private DateTimeOffset _expiresUtc;

    public ChallengeResponse CreateChallenge()
    {
        lock (_gate)
        {
            _challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            _expiresUtc = DateTimeOffset.UtcNow.AddMinutes(5);

            return new ChallengeResponse(
                _rsa.ExportSubjectPublicKeyInfoPem(),
                _challenge,
                _expiresUtc);
        }
    }

    public RecoveryCredentials DecryptAndConsume(string ciphertextBase64)
    {
        if (string.IsNullOrWhiteSpace(ciphertextBase64))
        {
            throw new InvalidOperationException("Encrypted payload is empty.");
        }

        byte[] cipher = Convert.FromBase64String(ciphertextBase64.Trim());
        byte[] clear = Array.Empty<byte>();

        try
        {
            clear = _rsa.Decrypt(cipher, RSAEncryptionPadding.OaepSHA256);
            var json = Encoding.UTF8.GetString(clear);
            var credentials = JsonSerializer.Deserialize<RecoveryCredentials>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Encrypted payload could not be decoded.");

            if (string.IsNullOrWhiteSpace(credentials.Username)
                || string.IsNullOrEmpty(credentials.Password)
                || string.IsNullOrWhiteSpace(credentials.Challenge))
            {
                throw new InvalidOperationException("Encrypted payload is incomplete.");
            }

            lock (_gate)
            {
                if (_challenge is null
                    || DateTimeOffset.UtcNow > _expiresUtc
                    || !CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(_challenge),
                        Encoding.UTF8.GetBytes(credentials.Challenge)))
                {
                    throw new InvalidOperationException("Recovery challenge is invalid or expired.");
                }

                _challenge = null;
                _expiresUtc = default;
            }

            return credentials;
        }
        finally
        {
            if (cipher.Length > 0)
            {
                CryptographicOperations.ZeroMemory(cipher);
            }

            if (clear.Length > 0)
            {
                CryptographicOperations.ZeroMemory(clear);
            }
        }
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}

public sealed record ChallengeResponse(
    string PublicKeyPem,
    string Challenge,
    DateTimeOffset ExpiresUtc);

public sealed record RecoveryCredentials(
    string Username,
    string Password,
    string Challenge);

public sealed record RecoveryRequest(string Ciphertext);

public sealed record RecoveryResult(
    bool Success,
    string Message,
    string AirAdminState,
    string Method,
    string Details);
