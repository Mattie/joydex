using System.IO.Pipes;

internal static class T3TransportFixture
{
    public static async Task<int> RunAsync(string mode, string readinessPipe)
    {
        // Readiness means the fixture has consumed the transport's entire stdin.
        _ = await Console.In.ReadToEndAsync();
        if (readinessPipe != "-")
        {
            using var pipe = new NamedPipeClientStream(".", readinessPipe, PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(timeout.Token);
            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        switch (mode)
        {
            case "finite":
                await Console.Error.WriteAsync("private stderr");
                await Console.Out.WriteAsync("{\"safe\":true}");
                return 0;
            case "fail":
                await Console.Error.WriteAsync("private token or prompt");
                return 2;
            case "flood":
                var chunk = new byte[64 * 1024];
                Array.Fill(chunk, (byte)'x');
                using (var output = Console.OpenStandardOutput())
                    for (var index = 0; index < 130; index++) await output.WriteAsync(chunk);
                break;
            case "wait":
                break;
            default:
                return 3;
        }

        // These modes must be stopped by transport cancellation or its output/deadline bound.
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}
