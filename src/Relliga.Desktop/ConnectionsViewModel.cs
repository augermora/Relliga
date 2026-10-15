using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Relliga.Application;
using Relliga.Domain;

namespace Relliga.Desktop;

public partial class MainViewModel
{
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(PauseAccountCommand))] private string connectionState = "Disconnected";
    [ObservableProperty] private string connectionDescription = "Connect Telegram once to keep supported cloud chats up to date while Relliga runs. Manual JSON imports remain available.";
    [ObservableProperty] private string lastSynchronization = "No successful synchronization yet";
    [ObservableProperty] private string connectedAccountLabel = "No authorized account identity";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(NeedsAuthorization))] [NotifyCanExecuteChangedFor(nameof(SubmitAuthorizationCommand))] private string? authorizationKind;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SubmitAuthorizationCommand))] private string authorizationInput = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RefreshAccountCommand),nameof(PauseAccountCommand),nameof(DisconnectAccountCommand))] private bool hasSavedConnection;
    [ObservableProperty] private string conversationFilter = "";
    [ObservableProperty] private string conversationSearch = "";
    [ObservableProperty] private string conversationPageLabel = "Select a conversation";
    [ObservableProperty] private ConversationSummary? selectedConversation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ConversationDetails))] private EventItem? selectedMessage;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyCanExecuteChangedFor(nameof(ConnectAccountCommand), nameof(PauseAccountCommand), nameof(RefreshAccountCommand), nameof(DisconnectAccountCommand), nameof(SubmitAuthorizationCommand))]
    private bool connectionAction;
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];
    public ObservableCollection<EventItem> Messages { get; } = [];
    public bool NeedsAuthorization => AuthorizationKind is not null;
    public bool CanConnect => IsReady && synchronization is not null && !ConnectionAction;
    public bool CanRefreshAccount => CanConnect && HasSavedConnection;
    public bool CanPauseAccount => CanRefreshAccount && ConnectionState != "Paused";
    public bool CanSubmitAuthorization => CanConnect && NeedsAuthorization && AuthorizationInput.Length > 0;
    public string ConversationDetails => SelectedMessage?.Details ?? "Select a message to inspect its source and identity. Attachments are not downloaded or opened.";
    private bool attachedSync;
    private int conversationPage;
    private long conversationTotal;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(OlderMessagesCommand), nameof(NewerMessagesCommand), nameof(SearchConversationCommand), nameof(CancelCommand))]
    private bool loadingConversation;
    public bool CanLoadConversation => SelectedConversation is not null && !LoadingConversation;
    public bool CanOlderMessages => CanLoadConversation && (long)(conversationPage + 1) * 50 < conversationTotal;
    public bool CanNewerMessages => CanLoadConversation && conversationPage > 0;
    private CancellationTokenSource? conversationCancellation;
    private async Task InitializeConnectionsAsync()
    {
        if (synchronization is null) return;
        if (!attachedSync)
        {
            attachedSync = true;
            synchronization.StatusChanged += status => Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                ConnectionState = status.State; ConnectionDescription = status.Description; AuthorizationKind = status.InputKind;
                if (status.State is "Connected" or "Paused" or "Disconnected" or "Authorization required") await RefreshConnectionsSafelyAsync();
            });
        }
        await synchronization.ResumeSavedAsync(); await RefreshConnectionsSafelyAsync();
        ConnectAccountCommand.NotifyCanExecuteChanged(); PauseAccountCommand.NotifyCanExecuteChanged(); RefreshAccountCommand.NotifyCanExecuteChanged(); DisconnectAccountCommand.NotifyCanExecuteChanged(); SubmitAuthorizationCommand.NotifyCanExecuteChanged();
    }
    private async Task RefreshConnectionsSafelyAsync()
    {
        try
        {
            var connection = await Task.Run(() => library.GetConnectionAsync("Telegram"));
            HasSavedConnection = connection is not null && connection.State != "Disconnected";
            ConnectedAccountLabel = connection?.AccountId is { } account ? "Authorized identity: " + account : "No authorized account identity";
            LastSynchronization = connection?.LastSuccess is { } time ? "Last successful synchronization: " + time.ToLocalTime().ToString("g") : "No successful synchronization yet";
            var conversations = await Task.Run(() => library.GetConversationsAsync(ConversationFilter));
            var key = SelectedConversation?.Key; Conversations.Clear(); foreach (var item in conversations) Conversations.Add(item);
            SelectedConversation = Conversations.FirstOrDefault(c => c.Key == key);
            if (IsConnections || IsConversations) await RefreshSummaryAsync();
        }
        catch (Exception ex) { Error = "Could not refresh connected-account information: " + ex.Message; }
    }
    private async Task ConnectionActionAsync(Func<Task> operation)
    {
        if (ConnectionAction) return; ConnectionAction = true; Error = null;
        try { await operation(); await RefreshConnectionsSafelyAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { ConnectionAction = false; }
    }
    [RelayCommand(CanExecute = nameof(CanConnect))] private Task ConnectAccountAsync() => ConnectionActionAsync(() => synchronization!.StartAsync());
    [RelayCommand(CanExecute = nameof(CanPauseAccount))] private Task PauseAccountAsync() => ConnectionActionAsync(() => synchronization!.PauseAsync());
    [RelayCommand(CanExecute = nameof(CanRefreshAccount))] private Task RefreshAccountAsync() => ConnectionActionAsync(() => synchronization!.RefreshAsync());
    [RelayCommand(CanExecute = nameof(CanRefreshAccount))] private Task DisconnectAccountAsync() => ConnectionActionAsync(async () =>
    {
        if (await AskAsync("Disconnect Telegram?", "Disconnect the live session and keep local history? Online logout is attempted. If offline, only the protected local key can be removed; revoke Relliga in Telegram Settings → Devices to terminate the server session. Use Library & export separately to remove stored messages.")) await synchronization!.DisconnectAsync();
    });
    [RelayCommand(CanExecute = nameof(CanSubmitAuthorization))] private Task SubmitAuthorizationAsync()
    {
        var input = AuthorizationInput; AuthorizationInput = "";
        return ConnectionActionAsync(() => synchronization!.AuthorizeAsync(input));
    }
    [RelayCommand] private Task FindConversationsAsync() => RefreshConnectionsSafelyAsync();
    partial void OnSelectedConversationChanged(ConversationSummary? value) { conversationPage = 0; _ = LoadConversationSafelyAsync(); }
    [RelayCommand(CanExecute = nameof(CanLoadConversation))] private async Task SearchConversationAsync() { conversationPage = 0; await LoadConversationSafelyAsync(); }
    [RelayCommand(CanExecute = nameof(CanOlderMessages))] private async Task OlderMessagesAsync() { conversationPage++; await LoadConversationSafelyAsync(); }
    [RelayCommand(CanExecute = nameof(CanNewerMessages))] private async Task NewerMessagesAsync() { conversationPage--; await LoadConversationSafelyAsync(); }
    private async Task LoadConversationSafelyAsync()
    {
        conversationCancellation?.Cancel(); var cancellation = new CancellationTokenSource(); conversationCancellation = cancellation;
        var selection = SelectedConversation; SelectedMessage = null;
        LoadingConversation = true;
        if (selection is null) { Messages.Clear(); ConversationPageLabel = "Select a conversation"; cancellation.Dispose(); if (ReferenceEquals(conversationCancellation,cancellation)) { conversationCancellation = null; LoadingConversation = false; } return; }
        ConversationPageLabel = "Loading conversation…";
        try
        {
            var result = await Task.Run(() => library.QueryAsync(new(Text: ConversationSearch, Provider: "Telegram", Kind: "Message", ConversationKey: selection.Key, Page: conversationPage), cancellation.Token));
            if (cancellation.IsCancellationRequested) return; Messages.Clear(); foreach (var row in result.Items) Messages.Add(new(row)); conversationTotal = result.Total;
            ConversationPageLabel = $"Page {conversationPage + 1:N0} · {result.Total:N0} matching messages · newest first";
            if (telegramProvider is not null && IsConversations && SelectedConversation?.Key == selection.Key)
            {
                var ids = result.Items.Select(row => JsonSerializer.Deserialize<ConversationMessage>(row.Event.MetadataJson)!.MessageId).ToArray();
                await telegramProvider.MarkViewedAsync(selection.Key, ids, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { if (ReferenceEquals(conversationCancellation,cancellation)) ConversationPageLabel = "Conversation query cancelled. Search again to retry."; }
        catch (Exception ex) { Error = "Conversation could not be loaded or its live read receipt failed: " + ex.Message; }
        finally { if (ReferenceEquals(conversationCancellation,cancellation)) { conversationCancellation = null; LoadingConversation = false; OlderMessagesCommand.NotifyCanExecuteChanged(); NewerMessagesCommand.NotifyCanExecuteChanged(); SearchConversationCommand.NotifyCanExecuteChanged(); } cancellation.Dispose(); }
    }
    public async Task StopConnectionsAsync()
    {
        conversationCancellation?.Cancel();
        if (synchronization is not null) await synchronization.DisposeAsync();
    }
    public async Task SetConnectionPresenceAsync(bool active)
    {
        try { if (telegramProvider is not null) await telegramProvider.SetWindowActiveAsync(active); }
        catch (Exception ex) { Error="Telegram presence could not be updated: "+ex.Message; }
    }
    partial void OnSelectedEventChanged(EventItem? value)
    {
        if (value is not null && value.Record.Provider=="Telegram" && IsExplorer) _ = MarkSelectedMessageAsync(value);
    }
    private async Task MarkSelectedMessageAsync(EventItem item)
    {
        try
        {
            var message=JsonSerializer.Deserialize<ConversationMessage>(item.Record.Event.MetadataJson)!;
            if (telegramProvider is not null) await telegramProvider.MarkViewedAsync(message.ConversationKey,[message.MessageId],CancellationToken.None);
        }
        catch (Exception ex) { Error="Telegram read receipt could not be sent: "+ex.Message; }
    }
}
