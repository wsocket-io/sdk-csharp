// wSocket C# / .NET SDK — Realtime Pub/Sub client with Presence, History and Connection Recovery.
//
// Usage:
//   var client = new wSocket.Client("ws://localhost:9001", "your-api-key");
//   await client.ConnectAsync();
//
//   var chat = client.Channel("chat:general");
//   chat.Subscribe((data, meta) => Console.WriteLine($"[{meta.Channel}] {data}"));
//
//   // Presence
//   chat.Presence.OnEnter(m => Console.WriteLine($"Joined: {m.ClientId}"));
//   chat.Presence.Enter(new { name = "Alice" });
//   chat.Presence.Get();
//
//   // History
//   chat.OnHistory(r => Console.WriteLine($"Got {r.Messages.Count} msgs"));
//   chat.History(new HistoryOptions { Limit = 50 });

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace wSocket;

// ─── Types ──────────────────────────────────────────────────

public record MessageMeta(string Id, string Channel, long Timestamp);

public record PresenceMember(string ClientId, JsonElement? Data, long JoinedAt)
{
    internal static PresenceMember FromJson(JsonElement json)
    {
        var clientId = json.TryGetProperty("clientId", out var cid) ? cid.GetString() ?? "" : "";
        var data = json.TryGetProperty("data", out var d) ? d : (JsonElement?)null;
        var joinedAt = json.TryGetProperty("joinedAt", out var ja) ? ja.GetInt64() : 0;
        return new PresenceMember(clientId, data, joinedAt);
    }
}

public record HistoryMessage(string Id, string Channel, JsonElement? Data, string PublisherId, long Timestamp, long Sequence);

public record HistoryResult(string Channel, List<HistoryMessage> Messages, bool HasMore);

public class HistoryOptions
{
    public int? Limit { get; set; }
    public long? Before { get; set; }
    public long? After { get; set; }
    public string? Direction { get; set; }
}

// ─── Presence ───────────────────────────────────────────────

public class Presence
{
    public string ChannelName { get; }
    private readonly Func<object, Task> _sendAsync;
    private readonly List<Action<PresenceMember>> _enterCbs = new();
    private readonly List<Action<PresenceMember>> _leaveCbs = new();
    private readonly List<Action<PresenceMember>> _updateCbs = new();
    private readonly List<Action<List<PresenceMember>>> _membersCbs = new();

    internal Presence(string channelName, Func<object, Task> sendAsync)
    {
        ChannelName = channelName;
        _sendAsync = sendAsync;
    }

    /// <summary>Enter the presence set with optional data.</summary>
    public Presence Enter(object? data = null)
    {
        _ = _sendAsync(new { action = "presence.enter", channel = ChannelName, data });
        return this;
    }

    /// <summary>Leave the presence set.</summary>
    public Presence Leave()
    {
        _ = _sendAsync(new { action = "presence.leave", channel = ChannelName });
        return this;
    }

    /// <summary>Update presence data.</summary>
    public Presence Update(object data)
    {
        _ = _sendAsync(new { action = "presence.update", channel = ChannelName, data });
        return this;
    }

    /// <summary>Get current presence members.</summary>
    public Presence Get()
    {
        _ = _sendAsync(new { action = "presence.get", channel = ChannelName });
        return this;
    }

    public Presence OnEnter(Action<PresenceMember> cb) { _enterCbs.Add(cb); return this; }
    public Presence OnLeave(Action<PresenceMember> cb) { _leaveCbs.Add(cb); return this; }
    public Presence OnUpdate(Action<PresenceMember> cb) { _updateCbs.Add(cb); return this; }
    public Presence OnMembers(Action<List<PresenceMember>> cb) { _membersCbs.Add(cb); return this; }

    internal void EmitEnter(PresenceMember m) { foreach (var cb in _enterCbs) { try { cb(m); } catch { } } }
    internal void EmitLeave(PresenceMember m) { foreach (var cb in _leaveCbs) { try { cb(m); } catch { } } }
    internal void EmitUpdate(PresenceMember m) { foreach (var cb in _updateCbs) { try { cb(m); } catch { } } }
    internal void EmitMembers(List<PresenceMember> members) { foreach (var cb in _membersCbs) { try { cb(members); } catch { } } }
}

// ─── Channel ────────────────────────────────────────────────

public class Channel
{
    public string Name { get; }
    public Presence Presence { get; }
    private readonly Func<object, Task> _sendAsync;
    private bool _subscribed;
    private readonly List<Action<JsonElement, MessageMeta>> _callbacks = new();
    private readonly List<Action<HistoryResult>> _historyCbs = new();

    internal Channel(string name, Func<object, Task> sendAsync)
    {
        Name = name;
        _sendAsync = sendAsync;
        Presence = new Presence(name, sendAsync);
    }

    /// <summary>Subscribe to messages on this channel.</summary>
    public Channel Subscribe(Action<JsonElement, MessageMeta> callback, int rewind = 0)
    {
        _callbacks.Add(callback);
        if (!_subscribed)
        {
            var msg = new Dictionary<string, object> { ["action"] = "subscribe", ["channel"] = Name };
            if (rewind > 0) msg["rewind"] = rewind;
            _ = _sendAsync(msg);
            _subscribed = true;
        }
        return this;
    }

    /// <summary>Publish data to this channel.</summary>
    public async Task PublishAsync(object data, bool persist = true)
    {
        var msg = new Dictionary<string, object>
        {
            ["action"] = "publish",
            ["channel"] = Name,
            ["data"] = data,
            ["id"] = Guid.NewGuid().ToString()
        };
        if (!persist) msg["persist"] = false;
        await _sendAsync(msg);
    }

    /// <summary>Query message history.</summary>
    public Channel History(HistoryOptions? opts = null)
    {
        var msg = new Dictionary<string, object> { ["action"] = "history", ["channel"] = Name };
        if (opts?.Limit != null) msg["limit"] = opts.Limit;
        if (opts?.Before != null) msg["before"] = opts.Before;
        if (opts?.After != null) msg["after"] = opts.After;
        if (opts?.Direction != null) msg["direction"] = opts.Direction;
        _ = _sendAsync(msg);
        return this;
    }

    /// <summary>Listen for history query results.</summary>
    public Channel OnHistory(Action<HistoryResult> callback)
    {
        _historyCbs.Add(callback);
        return this;
    }

    /// <summary>Unsubscribe from this channel.</summary>
    public async Task UnsubscribeAsync()
    {
        await _sendAsync(new { action = "unsubscribe", channel = Name });
        _subscribed = false;
        _callbacks.Clear();
        _historyCbs.Clear();
    }

    internal void Emit(JsonElement data, MessageMeta meta)
    {
        foreach (var cb in _callbacks) { try { cb(data, meta); } catch { } }
    }

    internal void EmitHistory(HistoryResult result)
    {
        foreach (var cb in _historyCbs) { try { cb(result); } catch { } }
    }

    internal void MarkForResubscribe() => _subscribed = false;
    internal bool HasListeners => _callbacks.Count > 0;
}

// ─── Client ─────────────────────────────────────────────────

public class Client : IAsyncDisposable
{
    private readonly string _url;
    private readonly string _apiKey;
    private readonly bool _autoReconnect;
    private readonly int _maxReconnectAttempts;
    private readonly TimeSpan _reconnectDelay;
    private readonly bool _recover;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Timer? _pingTimer;
    private readonly ConcurrentDictionary<string, Channel> _channels = new();
    private long _lastMessageTimestamp;
    private string? _resumeToken;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Client(
        string url,
        string apiKey,
        bool autoReconnect = true,
        int maxReconnectAttempts = 10,
        TimeSpan? reconnectDelay = null,
        bool recover = true)
    {
        _url = url;
        _apiKey = apiKey;
        _autoReconnect = autoReconnect;
        _maxReconnectAttempts = maxReconnectAttempts;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1);
        _recover = recover;
        PubSub = new PubSubNamespace(this);
    }

    /// <summary>Pub/Sub namespace — client.PubSub.Channel("name")</summary>
    public PubSubNamespace PubSub { get; }

    /// <summary>Push namespace — set via client.ConfigurePush()</summary>
    public PushClient? Push { get; private set; }

    /// <summary>Configure push notification access.</summary>
    public PushClient ConfigurePush(string baseUrl, string token, string appId)
    {
        Push = new PushClient(baseUrl, token, appId);
        return Push;
    }

    /// <summary>Connect to the wSocket server.</summary>
    public async Task ConnectAsync()
    {
        _ws = new ClientWebSocket();
        _cts = new CancellationTokenSource();
        var uri = new Uri($"{_url}/?key={_apiKey}");
        await _ws.ConnectAsync(uri, _cts.Token);
        StartPing();
        _ = Task.Run(() => ReadLoop(_cts.Token));
        await ResubscribeAll();
    }

    /// <summary>Disconnect from the server.</summary>
    public async Task DisconnectAsync()
    {
        StopPing();
        _cts?.Cancel();
        if (_ws?.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    /// <summary>Get or create a channel reference.</summary>
    public Channel Channel(string name)
    {
        return _channels.GetOrAdd(name, n => new Channel(n, SendAsync));
    }

    private async Task SendAsync(object msg)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var json = JsonSerializer.Serialize(msg, _jsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private void StartPing()
    {
        StopPing();
        _pingTimer = new Timer(_ =>
        {
            _ = SendAsync(new { action = "ping" });
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void StopPing()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[16384];
        try
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                HandleMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[wSocket] Read error: {ex.Message}");
            if (_autoReconnect) await Reconnect();
        }
    }

    private void HandleMessage(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (!root.TryGetProperty("action", out var actionProp)) return;
        var action = actionProp.GetString();
        var channel = root.TryGetProperty("channel", out var chProp) ? chProp.GetString() ?? "" : "";

        switch (action)
        {
            case "message":
            {
                if (_channels.TryGetValue(channel, out var ch))
                {
                    var data = root.GetProperty("data");
                    var id = root.TryGetProperty("id", out var idP) ? idP.GetString() ?? "" : "";
                    var ts = root.TryGetProperty("timestamp", out var tsP) ? tsP.GetInt64() : 0;
                    if (ts > _lastMessageTimestamp) _lastMessageTimestamp = ts;
                    ch.Emit(data, new MessageMeta(id, channel, ts));
                }
                break;
            }
            case "presence.enter":
            {
                if (_channels.TryGetValue(channel, out var ch) && root.TryGetProperty("data", out var d))
                    ch.Presence.EmitEnter(PresenceMember.FromJson(d));
                break;
            }
            case "presence.leave":
            {
                if (_channels.TryGetValue(channel, out var ch) && root.TryGetProperty("data", out var d))
                    ch.Presence.EmitLeave(PresenceMember.FromJson(d));
                break;
            }
            case "presence.update":
            {
                if (_channels.TryGetValue(channel, out var ch) && root.TryGetProperty("data", out var d))
                    ch.Presence.EmitUpdate(PresenceMember.FromJson(d));
                break;
            }
            case "presence.members":
            {
                if (_channels.TryGetValue(channel, out var ch) && root.TryGetProperty("data", out var arr))
                {
                    var members = new List<PresenceMember>();
                    foreach (var el in arr.EnumerateArray())
                        members.Add(PresenceMember.FromJson(el));
                    ch.Presence.EmitMembers(members);
                }
                break;
            }
            case "history":
            {
                if (_channels.TryGetValue(channel, out var ch) && root.TryGetProperty("data", out var d))
                    ch.EmitHistory(ParseHistoryResult(d, channel));
                break;
            }
            case "ack":
            {
                if (root.TryGetProperty("id", out var idP) && idP.GetString() == "resume"
                    && root.TryGetProperty("data", out var d)
                    && d.TryGetProperty("resumeToken", out var rt))
                {
                    _resumeToken = rt.GetString();
                }
                break;
            }
            case "error":
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                Console.Error.WriteLine($"[wSocket] Error: {err}");
                break;
            }
        }
    }

    private async Task ResubscribeAll()
    {
        if (_recover && _lastMessageTimestamp > 0)
        {
            var names = new List<string>();
            foreach (var ch in _channels.Values)
            {
                if (ch.HasListeners)
                {
                    names.Add(ch.Name);
                    ch.MarkForResubscribe();
                }
            }
            if (names.Count > 0)
            {
                var token = _resumeToken;
                if (string.IsNullOrEmpty(token))
                {
                    var payload = JsonSerializer.Serialize(new { channels = names, lastTs = _lastMessageTimestamp });
                    token = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                }
                await SendAsync(new { action = "resume", resumeToken = token });
                return;
            }
        }
        foreach (var ch in _channels.Values)
        {
            ch.MarkForResubscribe();
            if (ch.HasListeners)
                await SendAsync(new { action = "subscribe", channel = ch.Name });
        }
    }

    private async Task Reconnect()
    {
        for (int i = 1; i <= _maxReconnectAttempts; i++)
        {
            var delay = _reconnectDelay * Math.Pow(2, i - 1);
            Console.Error.WriteLine($"[wSocket] Reconnecting in {delay.TotalSeconds}s (attempt {i}/{_maxReconnectAttempts})");
            await Task.Delay(delay);
            try
            {
                await ConnectAsync();
                return;
            }
            catch { continue; }
        }
        Console.Error.WriteLine("[wSocket] Max reconnect attempts reached");
    }

    private static HistoryResult ParseHistoryResult(JsonElement json, string channel)
    {
        var messages = new List<HistoryMessage>();
        var hasMore = false;
        if (json.TryGetProperty("channel", out var chP)) channel = chP.GetString() ?? channel;
        if (json.TryGetProperty("hasMore", out var hm)) hasMore = hm.GetBoolean();
        if (json.TryGetProperty("messages", out var arr))
        {
            foreach (var el in arr.EnumerateArray())
            {
                messages.Add(new HistoryMessage(
                    el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    el.TryGetProperty("channel", out var c) ? c.GetString() ?? channel : channel,
                    el.TryGetProperty("data", out var d) ? d : null,
                    el.TryGetProperty("publisherId", out var pid) ? pid.GetString() ?? "" : "",
                    el.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0,
                    el.TryGetProperty("sequence", out var seq) ? seq.GetInt64() : 0
                ));
            }
        }
        return new HistoryResult(channel, messages, hasMore);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _ws?.Dispose();
    }
}

// ─── Push Notifications ─────────────────────────────────────

/// <summary>REST-based push notification client for wSocket.</summary>
/// <example>
/// var push = new PushClient("http://localhost:9001", "admin-token", "app-id");
/// await push.RegisterFCMAsync("device-token", "user1");
/// await push.SendToMemberAsync("user1", "Hello", "World");
/// </example>
public class PushClient
{
    private readonly string _baseUrl;
    private readonly string _token;
    private readonly string _appId;
    private readonly HttpClient _http = new();

    public PushClient(string baseUrl, string token, string appId)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _token = token;
        _appId = appId;
    }

    private async Task<JsonElement> ApiAsync(string method, string path, object? body = null)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), _baseUrl + path);
        req.Headers.Add("Authorization", $"Bearer {_token}");
        if (body != null)
            req.Content = new StringContent(
                JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Push API error {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    /// <summary>Register an FCM device token (Android).</summary>
    public async Task<string> RegisterFCMAsync(string deviceToken, string? memberId = null)
    {
        var res = await ApiAsync("POST", $"/api/admin/apps/{_appId}/push/register",
            new { platform = "fcm", memberId, deviceToken });
        return res.GetProperty("subscriptionId").GetString()!;
    }

    /// <summary>Register an APNs device token (iOS).</summary>
    public async Task<string> RegisterAPNsAsync(string deviceToken, string? memberId = null)
    {
        var res = await ApiAsync("POST", $"/api/admin/apps/{_appId}/push/register",
            new { platform = "apns", memberId, deviceToken });
        return res.GetProperty("subscriptionId").GetString()!;
    }

    /// <summary>Unregister push subscriptions for a member.</summary>
    public async Task<int> UnregisterAsync(string memberId, string? platform = null)
    {
        var res = await ApiAsync("DELETE", $"/api/admin/apps/{_appId}/push/unregister",
            new { memberId, platform });
        return res.GetProperty("removed").GetInt32();
    }

    /// <summary>Delete a specific push subscription by its ID.</summary>
    public async Task<bool> DeleteSubscriptionAsync(string subscriptionId)
    {
        var res = await ApiAsync("DELETE", $"/api/admin/apps/{_appId}/push/subscriptions/{subscriptionId}");
        return res.TryGetProperty("deleted", out var d) && d.GetBoolean();
    }

    /// <summary>Send a push notification to a specific member.</summary>
    public async Task<JsonElement> SendToMemberAsync(string memberId, string title, string? body = null)
    {
        return await ApiAsync("POST", $"/api/admin/apps/{_appId}/push/send",
            new { memberId, title, body });
    }

    /// <summary>Broadcast a push notification to all app subscribers.</summary>
    public async Task<JsonElement> BroadcastAsync(string title, string? body = null)
    {
        return await ApiAsync("POST", $"/api/admin/apps/{_appId}/push/send",
            new { broadcast = true, title, body });
    }
}

// ─── PubSub Namespace ───────────────────────────────────────

/// <summary>Scoped API for pub/sub operations.</summary>
public class PubSubNamespace
{
    private readonly Client _client;

    internal PubSubNamespace(Client client)
    {
        _client = client;
    }

    /// <summary>Get or create a channel (same as client.Channel()).</summary>
    public Channel Channel(string name)
    {
        return _client.Channel(name);
    }
}
