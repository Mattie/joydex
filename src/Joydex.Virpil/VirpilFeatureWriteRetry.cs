namespace Joydex.Virpil;

public static class VirpilFeatureWriteRetry
{
    public static void Send(
        byte[] logicalReport,
        Action<byte[]> write,
        Action reopen)
    {
        ArgumentNullException.ThrowIfNull(logicalReport);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(reopen);
        if (logicalReport.Length != VirpilLedProtocol.ReportLength)
        {
            throw new ArgumentException(
                $"A logical VIRPIL report must contain {VirpilLedProtocol.ReportLength} bytes.",
                nameof(logicalReport));
        }

        Exception? firstFailure = null;
        for (var openAttempt = 0; openAttempt < 2; openAttempt++)
        {
            foreach (var length in new[] { 38, 39 })
            {
                var payload = new byte[length];
                logicalReport.CopyTo(payload, 0);
                try
                {
                    write(payload);
                    return;
                }
                catch (Exception exception)
                {
                    firstFailure ??= exception;
                }
            }

            if (openAttempt == 0)
            {
                reopen();
            }
        }

        throw new IOException(
            "VIRPIL SetFeature failed for 38/39 bytes before and after reopening the device.",
            firstFailure);
    }
}
