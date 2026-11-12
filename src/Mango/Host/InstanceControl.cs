using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Mango.Host;

internal sealed class InstanceControl(string directory)
{
    private readonly string lockPath = Path.Combine(directory, "host.lock");
    private readonly string pipeName =
        "Mango-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.ToUpperInvariant())));

    public FileStream? TryAcquire()
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }
    }

    public async Task<string> ContactAsync(bool quit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(new byte[] { quit ? (byte)'Q' : (byte)'U' }, timeout.Token);
            using var reader = new StreamReader(pipe);
            return await reader.ReadLineAsync(timeout.Token)
                ?? throw new IOException("The running Mango host closed its control connection.");
        }
        catch (OperationCanceledException)
        {
            throw new IOException($"The running Mango host did not respond. Check {Path.Combine(directory, "startup-error.txt")}.");
        }
    }

    public async Task ServeAsync(string browserUrl, IHostApplicationLifetime lifetime)
    {
        var stopping = lifetime.ApplicationStopping;
        while (!stopping.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(stopping);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                var command = new byte[1];
                if (await pipe.ReadAsync(command, timeout.Token) == 0)
                    continue;
                if (command[0] is not ((byte)'Q' or (byte)'U'))
                    continue;
                var quit = command[0] == 'Q';
                await pipe.WriteAsync(Encoding.UTF8.GetBytes((quit ? "OK" : browserUrl) + "\n"), timeout.Token);
                if (quit)
                    lifetime.StopApplication();
            }
            catch (IOException)
            {
                // A launcher may exit before it reads the response.
            }
            catch (OperationCanceledException)
            {
                // Idle clients time out; application shutdown ends the loop.
            }
        }
    }
}
