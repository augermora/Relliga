using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Relliga.Application;

namespace Relliga.Infrastructure;

public interface ITdClient : IAsyncDisposable
{
    Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct);
    IAsyncEnumerable<JsonElement> Updates(CancellationToken ct);
}
public interface ITdClientFactory { ITdClient Create(); }
public sealed class NativeTdClientFactory(string nativePath) : ITdClientFactory { public ITdClient Create() => new TdJsonClient(nativePath); }

// Uses the official per-client JSON C ABI. One receive loop preserves TDLib update order.
public sealed class TdJsonClient : ITdClient
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateClient();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Send(nint client, [MarshalAs(UnmanagedType.LPUTF8Str)] string request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Receive(nint client, double timeout);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Execute(nint client, [MarshalAs(UnmanagedType.LPUTF8Str)] string request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(nint client);
    private readonly nint library;
    private readonly nint client;
    private readonly Send send;
    private readonly Receive receive;
    private readonly Destroy destroy;
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<(JsonElement Value,int Bytes)> updates = Channel.CreateBounded<(JsonElement,int)>(512);
    private long queuedBytes;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    private readonly Task pump;
    private long sequence;
    private bool disposed;
    private readonly object nativeGate = new();
    public TdJsonClient(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) throw new DllNotFoundException("Telegram TDLib is unavailable. The publisher must supply an official compatible tdjson library and configure its absolute path. Manual JSON import remains available.");
        library = NativeLibrary.Load(path, typeof(TdJsonClient).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        try
        {
            T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            send = Function<Send>("td_json_client_send"); receive = Function<Receive>("td_json_client_receive"); destroy = Function<Destroy>("td_json_client_destroy");
            var execute = Function<Execute>("td_json_client_execute");
            foreach (var request in new[] { "{\"@type\":\"setLogVerbosityLevel\",\"new_verbosity_level\":0}", "{\"@type\":\"setLogStream\",\"log_stream\":{\"@type\":\"logStreamEmpty\"}}" })
            {
                var response = execute(0,request); if (response == 0) throw new InvalidDataException("TDLib did not accept private logging configuration.");
                using var result = JsonDocument.Parse(Marshal.PtrToStringUTF8(response)!);
                if (result.RootElement.GetProperty("@type").GetString() != "ok") throw new InvalidDataException("TDLib logging could not be disabled. No session was opened.");
            }
            client = Function<CreateClient>("td_json_client_create")(); if (client == 0) throw new InvalidOperationException("TDLib could not create a client.");
            pump = Task.Run(Pump);
        }
        catch { NativeLibrary.Free(library); throw; }
    }
    private void Pump()
    {
        Exception? failure = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var pointer = receive(client, 0.25); if (pointer == 0) continue;
                int length = 0; while (length <= 4 * 1024 * 1024 && Marshal.ReadByte(pointer, length) != 0) length++;
                if (length > 4 * 1024 * 1024) throw new InvalidDataException("TDLib response exceeds the 4 MiB safety limit. Synchronization stopped; no page checkpoint was advanced.");
                using var doc = JsonDocument.Parse(Marshal.PtrToStringUTF8(pointer, length)!, new() { MaxDepth = 64 }); var json = doc.RootElement.Clone();
                if (json.TryGetProperty("@extra", out var extra) && extra.TryGetInt64(out var key) && pending.TryRemove(key, out var completion)) completion.TrySetResult(json);
                else if (json.TryGetProperty("@type", out var type) && type.GetString()?.StartsWith("update", StringComparison.Ordinal) == true)
                {
                    if (Interlocked.Add(ref queuedBytes,length) > 16 * 1024 * 1024 || !updates.Writer.TryWrite((json,length)))
                    { Interlocked.Add(ref queuedBytes,-length); throw new InvalidDataException("TDLib update queue reached its 512-update / 16 MiB limit. Reconnect to reconcile persisted messages before resuming."); }
                }
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var item in pending.Values) item.TrySetException(failure ?? new OperationCanceledException("TDLib stopped."));
            updates.Writer.TryComplete(failure);
        }
    }
    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested();
        var fields = parameters is null ? new Dictionary<string, JsonElement>() : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(parameters))!;
        var key = Interlocked.Increment(ref sequence); fields["@type"] = JsonSerializer.SerializeToElement(method); fields["@extra"] = JsonSerializer.SerializeToElement(key);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (pending.Count >= 32) throw new InvalidOperationException("Too many concurrent Telegram requests."); pending[key] = completion;
        try
        {
            lock (nativeGate) { ObjectDisposedException.ThrowIf(disposed, this); send(client, JsonSerializer.Serialize(fields)); }
            JsonElement result;
            try { result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), ct); }
            catch (TimeoutException) { throw new ProviderFailure("Telegram did not respond; the library remains available offline."); }
            if (result.GetProperty("@type").GetString() == "error")
            {
                var code = result.GetProperty("code").GetInt32();
                var description = result.TryGetProperty("message", out var error) ? error.GetString() ?? "" : "";
                var wait = System.Text.RegularExpressions.Regex.Match(description, @"(?:FLOOD_WAIT_|retry after )(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var seconds = wait.Success && int.TryParse(wait.Groups[1].Value, out var duration) ? Math.Clamp(duration, 1, 86400) : 60;
                // Never propagate raw provider errors: they may contain submitted authentication material.
                throw new TdRequestException(code, code == 429 ? TimeSpan.FromSeconds(seconds) : null);
            }
            return result;
        }
        finally { pending.TryRemove(key, out _); }
    }
    public async IAsyncEnumerable<JsonElement> Updates([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    { await foreach (var update in updates.Reader.ReadAllAsync(ct)) { Interlocked.Add(ref queuedBytes,-update.Bytes); yield return update.Value; } }
    public async ValueTask DisposeAsync()
    {
        lock (nativeGate) { if (disposed) return; disposed = true; send(client, "{\"@type\":\"close\"}"); }
        stop.Cancel(); await pump; destroy(client); NativeLibrary.Free(library); stop.Dispose();
    }
}
public sealed class TdRequestException(int code, TimeSpan? retryAfter = null) : IOException($"Telegram request failed (code {code}).")
{
    public int Code { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
