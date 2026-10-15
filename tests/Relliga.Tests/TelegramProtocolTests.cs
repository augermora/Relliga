using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Relliga.Application;
using Relliga.Domain;
using Relliga.Infrastructure;
using Xunit;

namespace Relliga.Tests;

public sealed class TelegramProtocolTests : IAsyncLifetime
{
    private readonly TestFiles files = new();
    private SqliteHistoryStore store = null!;
    public async ValueTask InitializeAsync() { store = new(Path.Combine(files.DirectoryPath,"library","relliga.db")); await store.InitializeAsync(); }
    public ValueTask DisposeAsync() { store.Dispose(); files.Dispose(); return ValueTask.CompletedTask; }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); while (!condition()) await Task.Delay(10,timeout.Token); }
    private static JsonElement Json(object value)
    {
        // C# escapes @type as an identifier; Telegram requires the literal JSON property @type.
        var raw = System.Text.RegularExpressions.Regex.Replace(JsonSerializer.Serialize(value), "\"type\"(?=:\")", "\"@type\"");
        using var document = JsonDocument.Parse(raw); return document.RootElement.Clone();
    }
    private async Task<(TelegramProvider Provider, FakeFactory Factory, MemoryVault Vault)> CreateAsync(bool authorize = false)
    {
        var vault = new MemoryVault(); await vault.WriteAsync("telegram-application",JsonSerializer.SerializeToUtf8Bytes(new TelegramApplicationCredentials(12345,new string('a',32))));
        var configuration = new TelegramConfiguration(vault,Path.Combine(files.DirectoryPath,"sessions"),store.DatabasePath,"synthetic-native-path");
        var factory = new FakeFactory(authorize); return (new(factory,configuration),factory,vault);
    }
    [Fact] public async Task AuthenticationTransitionsAndLiveEditsDeletionsUseTheRealProviderAndStore()
    {
        var setup = await CreateAsync(); await using var provider = setup.Provider;
        var states = new ConcurrentQueue<ConnectionStatus>(); provider.StatusChanged += states.Enqueue;
        var connection = new ConnectionRecord(Guid.NewGuid().ToString("N"),"Telegram",true,"Connecting"); await store.SaveConnectionAsync(connection);
        using var stop = new CancellationTokenSource(); var running = provider.RunAsync(connection,store,stop.Token);
        await Until(()=>states.Any(s=>s.InputKind=="Phone number"));
        await provider.AuthorizeAsync("+15550000000",default); await Until(()=>states.Any(s=>s.InputKind=="Verification code"));
        await provider.AuthorizeAsync("12345",default); await Until(()=>states.Any(s=>s.InputKind=="Password"));
        await provider.AuthorizeAsync("synthetic-password",default); await Until(()=>states.Any(s=>s.State=="Connected"));
        Assert.Single((await store.QueryAsync(new(Provider:"Telegram"))).Items);
        var client = setup.Factory.Clients.Single(); client.Text="Synthetic updated text"; client.Edited=1735992000;
        client.Push(new { @type="updateMessageContent",chat_id=8001L,message_id=1L<<20,new_content=new { @type="messageText",text=new {text=client.Text} } });
        await Until(()=>client.Calls.Any(c=>c=="getMessage"));
        await UntilAsync(async ()=>(await store.QueryAsync(new(Text:"updated",Provider:"Telegram"))).Total==1);
        client.Push(new { @type="updateDeleteMessages",chat_id=8001L,message_ids=new[]{1L<<20},is_permanent=true,from_cache=true });
        await Task.Delay(30); Assert.Single((await store.QueryAsync(new(Provider:"Telegram"))).Items);
        client.Push(new { @type="updateDeleteMessages",chat_id=8001L,message_ids=new[]{1L<<20},is_permanent=true,from_cache=false });
        await UntilAsync(async ()=>(await store.QueryAsync(new(Provider:"Telegram"))).Total==0);
        Assert.True((await store.VerifyAsync()).Healthy);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>running);
        var database = await File.ReadAllTextAsync(store.DatabasePath); Assert.DoesNotContain("synthetic-password",database); Assert.DoesNotContain("+15550000000",database);
    }
    private static async Task UntilAsync(Func<Task<bool>> condition)
    { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10)); while(!await condition()) await Task.Delay(10,timeout.Token); }
    [Fact] public async Task SavedConnectionRestartsWithSameKeyAndCheckpointAndPauseStopsTheWorker()
    {
        var setup=await CreateAsync(true); await using var engine=new SynchronizationService(store,setup.Provider);
        await engine.StartAsync(); await Until(()=>engine.Status.State=="Connected");
        var connection=(await store.GetConnectionAsync("Telegram"))!; Assert.Equal("user9001",connection.AccountId); Assert.NotNull(connection.LastSuccess);
        Assert.StartsWith("head:",(await store.GetCheckpointAsync(connection.Id,"history/8001"))!);
        var keys=setup.Vault.Values.Where(p=>p.Key.StartsWith("telegram-session/",StringComparison.Ordinal)).Select(p=>p.Value.ToArray()).ToArray(); Assert.Single(keys);
        await engine.PauseAsync(); Assert.False((await store.GetConnectionAsync("Telegram"))!.Automatic); var count=setup.Factory.Clients.Count;
        await engine.StartAsync(); await Until(()=>setup.Factory.Clients.Count>count && engine.Status.State=="Connected");
        Assert.Equal(keys[0],setup.Vault.Values.Single(p=>p.Key.StartsWith("telegram-session/",StringComparison.Ordinal)).Value);
        Assert.Single((await store.QueryAsync(new(Provider:"Telegram"))).Items);
        await engine.DisconnectAsync(); Assert.Equal("Disconnected",(await store.GetConnectionAsync("Telegram"))!.State); Assert.Single((await store.QueryAsync(new(Provider:"Telegram"))).Items);
    }
    [Fact] public async Task RateLimitRetriesWithBackoffAndCancellationInterruptsWaiting()
    {
        var provider=new RateLimitedProvider(); await using var engine=new SynchronizationService(store,provider);
        await engine.StartAsync(); await Until(()=>provider.Attempts>=2); Assert.Equal("Retrying",engine.Status.State);
        await engine.PauseAsync(); var attempts=provider.Attempts; await Task.Delay(40); Assert.Equal(attempts,provider.Attempts);
    }
    [Fact] public async Task ManualRefreshOfPausedConnectionCompletesOnePassAndPreservesPause()
    {
        var setup=await CreateAsync(true); setup.Factory.Configure=c=>c.DisposeDelay=100; await using var engine=new SynchronizationService(store,setup.Provider);
        await store.SaveConnectionAsync(new(Guid.NewGuid().ToString("N"),"Telegram",false,"Paused"));
        await engine.RefreshAsync(); await Until(()=>engine.Status.State=="Paused");
        var connection=(await store.GetConnectionAsync("Telegram"))!; Assert.False(connection.Automatic); Assert.NotNull(connection.LastSuccess);
        Assert.Single((await store.QueryAsync(new(Provider:"Telegram"))).Items);
        await engine.StartAsync(); await Until(()=>engine.Status.State=="Connected"); Assert.True((await store.GetConnectionAsync("Telegram"))!.Automatic);
    }
    [Fact] public async Task ReadReceiptsStayWithinTheAuthorizedAccountAndPresenceTracksActivation()
    {
        var setup=await CreateAsync(true); await using var engine=new SynchronizationService(store,setup.Provider);
        await engine.StartAsync(); await Until(()=>engine.Status.State=="Connected"); var client=setup.Factory.Clients.Single();
        await setup.Provider.MarkViewedAsync("user9999:8001",[1],default); Assert.DoesNotContain(client.Calls,c=>c=="viewMessages");
        await setup.Provider.MarkViewedAsync("user9001:8001",[1],default); Assert.Single(client.Requests,r=>r.Method=="viewMessages");
        var receipt=client.Requests.Single(r=>r.Method=="viewMessages").Parameters; Assert.True(receipt.GetProperty("force_read").GetBoolean()); Assert.Equal(1L<<20,receipt.GetProperty("message_ids")[0].GetInt64());
        await setup.Provider.SetWindowActiveAsync(true); await setup.Provider.SetWindowActiveAsync(false);
        var presence=client.Requests.Where(r=>r.Method=="setOption").Last().Parameters; Assert.False(presence.GetProperty("value").GetProperty("value").GetBoolean());
    }
    [Fact] public async Task FutureOrUnsupportedAuthorizationStateIsExplicitAndDoesNotClaimConnection()
    {
        var setup=await CreateAsync(); await using var engine=new SynchronizationService(store,setup.Provider);
        await engine.StartAsync(); await Until(()=>setup.Factory.Clients.Count==1);
        setup.Factory.Clients.Single().Push(new { @type="updateAuthorizationState",authorization_state=new { @type="authorizationStateWaitRegistration" } });
        await Until(()=>engine.Status.State=="Reauthorization required"); Assert.Null((await store.GetConnectionAsync("Telegram"))!.LastSuccess);
    }
    [Fact] public async Task DifferentAuthorizedAccountCannotReuseAnotherAccountsCheckpointNamespace()
    {
        var setup=await CreateAsync(true); await store.SaveConnectionAsync(new(Guid.NewGuid().ToString("N"),"Telegram",true,"Connected",AccountId:"user9999"));
        await using var engine=new SynchronizationService(store,setup.Provider); await engine.StartAsync(); await Until(()=>engine.Status.State=="Reauthorization required");
        Assert.Empty((await store.QueryAsync(new(Provider:"Telegram"))).Items); Assert.Equal("user9999",(await store.GetConnectionAsync("Telegram"))!.AccountId);
    }
    [Fact] public async Task MissingProtectedApplicationConfigurationAndNativeLibraryFailActionably()
    {
        var config=new TelegramConfiguration(new MemoryVault(),Path.Combine(files.DirectoryPath,"sessions"),store.DatabasePath,"missing");
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>config.GetApplicationAsync(default)); Assert.Contains("publisher",error.Message);
        Assert.Throws<DllNotFoundException>(()=>new TdJsonClient(Path.Combine(files.DirectoryPath,"missing-tdjson.dll")));
    }
    [Theory] [InlineData("secret")] [InlineData("channel")] [InlineData("protected")] [InlineData("ephemeral")] [InlineData("unsavable")]
    public async Task PrivacyAndUnsupportedChatVariantsNeverPersistMessageContent(string variant)
    {
        var setup = await CreateAsync(true);
        setup.Factory.Configure = c => { c.ChatType = variant == "secret" ? "chatTypeSecret" : variant == "channel" ? "chatTypeSupergroup" : "chatTypePrivate"; c.Channel = variant == "channel"; c.Protected = variant == "protected"; c.Ephemeral = variant == "ephemeral"; c.CanSave = variant != "unsavable"; };
        await using var engine = new SynchronizationService(store,setup.Provider); await engine.StartAsync(); await Until(()=>engine.Status.State=="Connected");
        Assert.Empty((await store.QueryAsync(new(Provider:"Telegram"))).Items); Assert.True((await store.VerifyAsync()).Healthy);
    }
    [Fact] public async Task DisconnectAfterRestartWhilePausedForgetsThePersistedKeyAndReportsUnconfirmedLogout()
    {
        var setup = await CreateAsync(true); var connection = new ConnectionRecord(Guid.NewGuid().ToString("N"),"Telegram",false,"Paused",AccountId:"user9001"); await store.SaveConnectionAsync(connection);
        var configuration = new TelegramConfiguration(setup.Vault,Path.Combine(files.DirectoryPath,"sessions"),store.DatabasePath,"missing");
        _ = await configuration.EncryptionKeyAsync(connection.Id,default); Assert.Contains(setup.Vault.Values.Keys,k=>k.StartsWith("telegram-session/",StringComparison.Ordinal));
        var outcome = await setup.Provider.DisconnectAsync(connection, default); Assert.Contains("Server logout could not be confirmed",outcome);
        Assert.DoesNotContain(setup.Vault.Values.Keys,k=>k.StartsWith("telegram-session/",StringComparison.Ordinal));
        await setup.Provider.DisposeAsync(); Assert.Empty((await store.QueryAsync(new(Provider:"Telegram"))).Items);
    }
    [Fact] public async Task EphemeralProtectedAndSecretChatMessagesNeverEnterTheOrdinaryLibrary()
    {
        var setup=await CreateAsync(true); await using var engine=new SynchronizationService(store,setup.Provider);
        await engine.StartAsync(); await Until(()=>engine.Status.State=="Connected"); var client=setup.Factory.Clients.Single();
        client.AutoDelete=30; client.Push(new { @type="updateChatMessageAutoDeleteTime",chat_id=8001L,message_auto_delete_time=30 });
        await UntilAsync(async ()=>(await store.QueryAsync(new(Provider:"Telegram"))).Total==0);
        Assert.True((await store.VerifyAsync()).Healthy);
    }
    private sealed class MemoryVault : ICredentialVault
    {
        public ConcurrentDictionary<string,byte[]> Values {get;}=new();
        public Task<byte[]?> ReadAsync(string key,CancellationToken ct=default)=>Task.FromResult(Values.TryGetValue(key,out var value)?value.ToArray():null);
        public Task WriteAsync(string key,byte[] secret,CancellationToken ct=default) {Values[key]=secret.ToArray();return Task.CompletedTask;}
        public Task DeleteAsync(string key,CancellationToken ct=default) {Values.TryRemove(key,out _);return Task.CompletedTask;}
    }
    private sealed class FakeFactory(bool authorized):ITdClientFactory
    {
        public ConcurrentBag<FakeTdClient> Clients {get;}=[];
        public Action<FakeTdClient>? Configure;
        public ITdClient Create(){var client=new FakeTdClient(authorized);Configure?.Invoke(client);Clients.Add(client);return client;}
    }
    private sealed class FakeTdClient:ITdClient
    {
        private readonly Channel<JsonElement> updates=System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private readonly bool authorized;
        public ConcurrentQueue<string> Calls {get;}=new();
        public ConcurrentQueue<(string Method,JsonElement Parameters)> Requests {get;}=new();
        public string Text="Synthetic live message";
        public long Edited;
        public int AutoDelete;
        public string ChatType="chatTypePrivate";
        public bool Channel;
        public bool Protected;
        public bool Ephemeral;
        public bool CanSave=true;
        public int DisposeDelay;
        public FakeTdClient(bool authorized){this.authorized=authorized;Push(new { @type="updateAuthorizationState",authorization_state=new { @type="authorizationStateWaitTdlibParameters" } });Push(new { @type="updateConnectionState",state=new { @type="connectionStateReady" } });}
        public void Push(object value)=>updates.Writer.TryWrite(Json(value));
        private void State(string type)=>Push(new { @type="updateAuthorizationState",authorization_state=new { @type=type } });
        private object Chat()=>new Dictionary<string,object>{["@type"]="chat",["id"]=8001L,["title"]="Synthetic live chat",["type"]=new { @type=ChatType,user_id=8001L,is_channel=Channel },["has_protected_content"]=Protected,["message_auto_delete_time"]=AutoDelete};
        private object Message()=>new { @type="message",id=1L<<20,chat_id=8001L,sender_id=new { @type="messageSenderUser",user_id=8001L },date=1735819200L,edit_date=Edited,can_be_saved=CanSave,is_channel_post=false,self_destruct_in=Ephemeral?10:0,auto_delete_in=AutoDelete,content=new { @type="messageText",text=new { @type="formattedText",text=Text,entities=Array.Empty<object>() } } };
        public Task<JsonElement> CallAsync(string method,object? parameters,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();Calls.Enqueue(method);var p=parameters is null?default:Json(parameters); Requests.Enqueue((method,p));
            object result=new { @type="ok" };
            switch(method)
            {
                case "setTdlibParameters": State(authorized?"authorizationStateReady":"authorizationStateWaitPhoneNumber");break;
                case "setAuthenticationPhoneNumber":State("authorizationStateWaitCode");break;
                case "checkAuthenticationCode":State("authorizationStateWaitPassword");break;
                case "checkAuthenticationPassword":State("authorizationStateReady");break;
                case "getMe":result=new { @type="user",id=9001L };break;
                case "loadChats":throw new TdRequestException(404);
                case "getChats":result=new { @type="chats",chat_ids=new[]{8001L} };break;
                case "getChat":result=Chat();break;
                case "getChatHistory":result=new { @type="messages",messages=p.GetProperty("from_message_id").GetInt64()==0?new[]{Message()}:Array.Empty<object>() };break;
                case "getMessages":result=new { @type="messages",messages=p.GetProperty("message_ids").EnumerateArray().Select(_=>Message()).ToArray() };break;
                case "getMessage":result=Message();break;
                case "logOut":State("authorizationStateClosed");break;
            }
            return Task.FromResult(Json(result));
        }
        public IAsyncEnumerable<JsonElement> Updates(CancellationToken ct)=>updates.Reader.ReadAllAsync(ct);
        public async ValueTask DisposeAsync(){updates.Writer.TryComplete();if(DisposeDelay>0)await Task.Delay(DisposeDelay);}
    }
    private sealed class RateLimitedProvider:IConnectedProvider
    {
        public string Name=>"Synthetic";public int Attempts;public event Action<ConnectionStatus>? StatusChanged {add{} remove{}}
        public Task RunAsync(ConnectionRecord connection,IConnectionStore store,CancellationToken ct,bool once=false){Interlocked.Increment(ref Attempts);throw new ProviderFailure("Synthetic rate limit",retryAfter:TimeSpan.FromMilliseconds(20));}
        public Task AuthorizeAsync(string input,CancellationToken ct)=>Task.CompletedTask;public Task<string> DisconnectAsync(ConnectionRecord? connection,CancellationToken ct)=>Task.FromResult("Synthetic disconnected");public void Refresh(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
