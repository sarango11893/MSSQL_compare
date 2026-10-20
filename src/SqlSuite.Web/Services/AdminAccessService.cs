using System.Security.Cryptography;
using System.Text;

namespace SqlSuite.Web.Services;

public sealed class AdminAccessService
{
    private readonly object _sync = new();
    private readonly byte[]? _expectedPasswordHash;
    private int _failedAttempts;
    private DateTimeOffset? _lockoutUntil;

    public AdminAccessService()
    {
        string? password = Environment.GetEnvironmentVariable("SQLSUITE_ADMIN_PASSWORD");
        if (!string.IsNullOrEmpty(password))
            _expectedPasswordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    public bool IsConfigured => _expectedPasswordHash is not null;

    public bool TryValidatePassword(string? password, out TimeSpan lockoutRemaining)
    {
        lockoutRemaining = TimeSpan.Zero;
        if (_expectedPasswordHash is null || string.IsNullOrEmpty(password))
            return false;

        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_lockoutUntil is DateTimeOffset lockoutUntil && lockoutUntil > now)
            {
                lockoutRemaining = lockoutUntil - now;
                return false;
            }

            byte[] candidateHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            bool valid;
            try
            {
                valid = CryptographicOperations.FixedTimeEquals(_expectedPasswordHash, candidateHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(candidateHash);
            }

            if (valid)
            {
                _failedAttempts = 0;
                _lockoutUntil = null;
                return true;
            }

            _failedAttempts++;
            if (_failedAttempts >= 5)
            {
                _failedAttempts = 0;
                _lockoutUntil = now.AddMinutes(1);
                lockoutRemaining = TimeSpan.FromMinutes(1);
            }

            return false;
        }
    }
}
