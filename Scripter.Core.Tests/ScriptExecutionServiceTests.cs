using Scripter.Core;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Scripter.Core.Tests;

public sealed class ScriptExecutionServiceTests
{
    [Fact]
    public async Task ExecutesExportedFunctionsWithARealDocumentPath()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "add.js");
        var request = ScriptExecutionRequest.ForFile(
            path,
            "function add(...args) { return args.reduce((sum, value) => sum + Number(value), 0); }",
            new ScriptInvocation("add", ["1", "2", "3"]));
        var service = new ScriptExecutionService();

        var result = await service.ExecuteAsync(request);

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal("6", result.Output);
    }

    [Fact]
    public async Task ErrorDetailsContainTheRealScriptPath()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "broken.js");
        var service = new ScriptExecutionService();

        var result = await service.ExecuteAsync(ScriptExecutionRequest.ForFile(path, "throw new Error('boom');"));

        Assert.False(result.IsSuccess);
        Assert.Contains("broken.js", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersistentSessionUsesAFreshEngineForEveryRun()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "state.js");
        var service = new ScriptExecutionService();
        using var session = service.CreateSession();

        var first = await session.ExecuteAsync(ScriptExecutionRequest.ForFile(path, "globalThis.leaked = 42; leaked;"));
        var second = await session.ExecuteAsync(ScriptExecutionRequest.ForFile(path, "typeof globalThis.leaked;"));

        Assert.True(first.IsSuccess, first.Error);
        Assert.True(second.IsSuccess, second.Error);
        Assert.Equal("undefined", second.Output);
    }

    [Fact]
    public async Task NativeFfiIsExposedOnlyWhenExplicitlyEnabled()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "ffi.js");
        var service = new ScriptExecutionService();
        var request = ScriptExecutionRequest.ForFile(
            path,
            "const mulDiv = ffi.bind('kernel32.dll', 'MulDiv', 'winapi i32(i32, i32, i32)'); mulDiv.Invoke(6, 7, 1);");

        var disabled = await service.ExecuteAsync(ScriptExecutionRequest.ForFile(path, "typeof ffi;"));
        var enabled = await service.ExecuteAsync(request, new ScriptExecutionOptions([], false, false, true));

        Assert.True(disabled.IsSuccess, disabled.Error);
        Assert.Equal("undefined", disabled.Output);
        Assert.True(enabled.IsSuccess, enabled.Error);
        Assert.Equal("42", enabled.Output);
    }

    [Fact]
    public async Task StopCancelsAWaitForDebugger()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "waiting.js");
        var service = new ScriptExecutionService();
        using var session = service.CreateSession(new ScriptDebugOptions(ScriptDebugMode.Wait, GetAvailablePort()));
        var execution = session.ExecuteAsync(ScriptExecutionRequest.ForFile(path, "1"));

        await Task.Delay(50);
        var stopped = session.TryRequestStop();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(stopped);
        Assert.True(result.WasCancelled);
    }

    [Fact]
    public async Task BreakModePausesInTheUserDocument()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "debug-target.js");
        var port = GetAvailablePort();
        var service = new ScriptExecutionService();
        using var session = service.CreateSession(new ScriptDebugOptions(ScriptDebugMode.Break, port));
        var execution = session.ExecuteAsync(
            ScriptExecutionRequest.ForFile(path, "const value = 40; value + 2;"),
            cancellationToken: timeout.Token);

        var target = await GetDebuggerTargetAsync(port, timeout.Token);
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(target), timeout.Token);
        await SendAsync(socket, "{\"id\":1,\"method\":\"Debugger.enable\"}", timeout.Token);

        var url = await ReceivePausedUrlAsync(socket, timeout.Token);
        await SendAsync(socket, "{\"id\":2,\"method\":\"Debugger.resume\"}", timeout.Token);
        var result = await execution;

        Assert.EndsWith("debug-target.js", url, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsSuccess, result.Error);
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> GetDebuggerTargetAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var json = await client.GetStringAsync($"http://127.0.0.1:{port}/json", cancellationToken);
                using var document = JsonDocument.Parse(json);
                var target = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement[0]
                    : document.RootElement;
                return target.GetProperty("webSocketDebuggerUrl").GetString()!;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private static Task SendAsync(ClientWebSocket socket, string message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    private static async Task<string?> ReceivePausedUrlAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var scriptUrls = new Dictionary<string, string>();
        while (true)
        {
            var message = await ReceiveMessageAsync(socket, cancellationToken);
            using var document = JsonDocument.Parse(message);
            if (!document.RootElement.TryGetProperty("method", out var method))
            {
                continue;
            }

            if (method.GetString() == "Debugger.scriptParsed")
            {
                var parameters = document.RootElement.GetProperty("params");
                scriptUrls[parameters.GetProperty("scriptId").GetString()!] = parameters.GetProperty("url").GetString() ?? string.Empty;
                continue;
            }

            if (method.GetString() == "Debugger.paused")
            {
                var scriptId = document.RootElement.GetProperty("params").GetProperty("callFrames")[0]
                    .GetProperty("location").GetProperty("scriptId").GetString()!;
                return scriptUrls.GetValueOrDefault(scriptId);
            }
        }
    }

    private static async Task<string> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
