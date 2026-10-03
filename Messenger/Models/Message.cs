namespace Messenger.Models;

public class Message
{
    public int id { get; set; }
    
    public int sender_id { get; set; }
    public int receiver_id { get; set; }
    
    public string text { get; set; }
    
    public DateTime created_at { get; set; }
    
    public bool is_edited { get; set; }
    public bool is_deleted { get; set; }
}