using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Messenger.Data;
using Messenger.Models;

namespace Messenger.Hubs;

public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private static readonly ConcurrentDictionary<string, int> Connections = new();

    public ChatHub(AppDbContext db)
    {
        _db = db;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Connections.TryRemove(Context.ConnectionId, out _);
        await SendUsers();
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinToMessage(string username)
    {
        username = username?.Trim();
        if (string.IsNullOrEmpty(username)) return;

        var user = await _db.Users.FirstOrDefaultAsync(u => u.username == username);
        if (user == null)
        {
            user = new User { username = username };
            _db.Users.Add(user);
            await _db.SaveChangesAsync();
        }

        Context.Items["UserId"] = user.id;
        Connections[Context.ConnectionId] = user.id;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user-{user.id}");

        await SendUsers();
    }

    private async Task SendUsers()
    {
        var onlineIds = Connections.Values.ToHashSet();

        var users = await _db.Users
            .Select(u => new { u.id, u.username })
            .ToListAsync();

        var result = users.Select(u => new
        {
            u.id,
            u.username,
            online = onlineIds.Contains(u.id)
        });

        await Clients.All.SendAsync("UpdateUsers", result);
    }

    public async Task SendPrivateMessage(int toUserId, string text)
    {
        if (!Context.Items.TryGetValue("UserId", out var value) || value is not int senderId)
            return;

        text = text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var sender = await _db.Users.FindAsync(senderId);
        var receiver = await _db.Users.FindAsync(toUserId);
        if (sender == null || receiver == null) return;

        var message = new Message
        {
            sender_id = senderId,
            receiver_id = toUserId,
            text = text,
            created_at = DateTime.UtcNow,
            is_edited = false,
            is_deleted = false
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        await Clients.Group($"user-{toUserId}")
            .SendAsync("ReceiveMessage", senderId, sender.username, message.id, message.text);

        await Clients.Caller
            .SendAsync("MessageSent", toUserId, message.id, message.text);
    }

    public async Task EditMessage(int messageId, string newText)
    {
        if (!Context.Items.TryGetValue("UserId", out var value) || value is not int myId)
            return;

        newText = newText?.Trim();
        if (string.IsNullOrEmpty(newText)) return;

        var message = await _db.Messages.FindAsync(messageId);
        if (message == null || message.sender_id != myId || message.is_deleted)
            return;

        message.text = newText;
        message.is_edited = true;
        await _db.SaveChangesAsync();

        await Clients.Groups($"user-{message.sender_id}", $"user-{message.receiver_id}")
            .SendAsync("MessageEdited", message.id, message.text);
    }

    public async Task DeleteMessage(int messageId)
    {
        if (!Context.Items.TryGetValue("UserId", out var value) || value is not int myId)
            return;

        var message = await _db.Messages.FindAsync(messageId);
        if (message == null || message.sender_id != myId || message.is_deleted)
            return;

        message.is_deleted = true;
        await _db.SaveChangesAsync();

        await Clients.Groups($"user-{message.sender_id}", $"user-{message.receiver_id}")
            .SendAsync("MessageDeleted", message.id);
    }

    public async Task<List<object>> GetHistory(int otherUserId)
    {
        if (!Context.Items.TryGetValue("UserId", out var value) || value is not int myId)
            return new();

        var messages = await _db.Messages
            .Where(m => !m.is_deleted &&
                        ((m.sender_id == myId && m.receiver_id == otherUserId) ||
                         (m.sender_id == otherUserId && m.receiver_id == myId)))
            .OrderBy(m => m.created_at)
            .Select(m => new
            {
                m.id,
                m.text,
                m.created_at,
                isEdited = m.is_edited,
                fromMe = m.sender_id == myId
            })
            .ToListAsync();

        return messages.Cast<object>().ToList();
    }
}