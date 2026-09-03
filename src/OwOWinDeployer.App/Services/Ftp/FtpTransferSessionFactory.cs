namespace OwOWinDeployer.App.Services.Ftp;

/// <summary>Creates an authenticated FTP session for one isolated transfer or directory scan.</summary>
public sealed class FtpTransferSessionFactory : IFtpTransferSessionFactory, IFtpDirectorySessionFactory
{
    public event Action<string>? Log;

    public async Task<IFtpTransferSession> ConnectAsync(
        FtpConnectionSnapshot connection, CancellationToken cancellationToken)
    {
        var client = new FtpClient();
        client.Log += OnLog;
        try
        {
            await client.ConnectAsync(connection.Host, connection.Port, connection.TlsMode,
                connection.UserName, connection.Password, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Log -= OnLog;
            client.Dispose();
            throw;
        }
    }

    private void OnLog(string line) => Log?.Invoke(line);

    async Task<IFtpDirectorySession> IFtpDirectorySessionFactory.ConnectAsync(
        FtpConnectionSnapshot connection, CancellationToken cancellationToken)
        => (IFtpDirectorySession)await ConnectAsync(connection, cancellationToken).ConfigureAwait(false);
}
