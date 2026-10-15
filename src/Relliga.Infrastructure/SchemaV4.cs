namespace Relliga.Infrastructure;

public sealed partial class SqliteHistoryStore
{
    private const string SchemaV4 = """
        CREATE TABLE connections(id TEXT PRIMARY KEY,provider TEXT NOT NULL UNIQUE,automatic INTEGER NOT NULL,state TEXT NOT NULL,last_success TEXT,account_id TEXT);
        CREATE TABLE sync_checkpoints(connection_id TEXT NOT NULL REFERENCES connections(id) ON DELETE CASCADE,scope TEXT NOT NULL,cursor TEXT,PRIMARY KEY(connection_id,scope));
        CREATE TABLE telegram_chats(account_id TEXT NOT NULL,chat_id INTEGER NOT NULL,title TEXT,type TEXT NOT NULL,PRIMARY KEY(account_id,chat_id));
        CREATE TABLE telegram_participants(account_id TEXT NOT NULL,participant_id TEXT NOT NULL,name TEXT,PRIMARY KEY(account_id,participant_id));
        CREATE TABLE telegram_messages(event_id INTEGER PRIMARY KEY REFERENCES events(id) ON DELETE CASCADE,account_id TEXT NOT NULL,chat_id INTEGER NOT NULL,message_id INTEGER NOT NULL,message_json TEXT NOT NULL,
          UNIQUE(account_id,chat_id,message_id),FOREIGN KEY(account_id,chat_id) REFERENCES telegram_chats(account_id,chat_id));
        CREATE INDEX ix_telegram_chat ON telegram_messages(account_id,chat_id,message_id);
        CREATE INDEX ix_telegram_sender ON telegram_messages(account_id,json_extract(message_json,'$.SenderId'));
        CREATE TABLE telegram_deletions(account_id TEXT NOT NULL,chat_id INTEGER NOT NULL,message_id INTEGER NOT NULL,PRIMARY KEY(account_id,chat_id,message_id));
        PRAGMA user_version=4;
        """;
}
