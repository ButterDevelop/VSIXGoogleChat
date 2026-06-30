using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VSIXGoogleChat.Services
{
    public interface IChatService
    {
        Task<string?> SendMessageAsync(string text, string? threadName = null, string? replyToMessageId = null);

        Task<List<ChatMessage>> GetMessagesAsync(DateTime? lastMessageTime = null, int maxCount = 50);
        Task<(List<ChatMessage> Messages, string? NextPageToken)> GetMessagesPageAsync(string? pageToken, int maxCount = 50);
        Task<List<ChatMessage>> GetMessagesForSpaceAsync(string spaceId, DateTime? lastMessageTime = null, int maxCount = 50);

        Task<System.IO.Stream?> DownloadAttachmentAsync(string resourceName);

        Task<List<ChatSpace>> GetSpacesAsync();
        void SetCurrentSpace(string spaceId);
        string GetCurrentSpace();
        Task<string?> SendMessageWithAttachmentAsync(string text, string filePath, string mimeType);
        Task<string?> SendMessageWithAttachmentsAsync(string text, List<string> filePaths);
        Task<bool> AddReactionAsync(string messageId, string emojiUnicode);
    }

    public class MessageTagInfo
    {
        public string MessageId { get; set; } = "";
        public string ThreadName { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public class ChatSpace : System.ComponentModel.INotifyPropertyChanged
    {
        public string Id { get; set; } = "";
        
        private string _name = "";
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
                }
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    public class ChatAttachment
    {
        public string Name { get; set; } = "";
        public string ContentName { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string ContentUri { get; set; } = "";
        public string MessageId { get; set; } = "";
    }

    public class ChatMessage
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string SenderName { get; set; } = "";
        public string SenderId { get; set; } = "";
        public DateTime CreateTime { get; set; }
        public bool HasAttachments { get; set; }

        public string ThreadName { get; set; } = "";
        public string QuotedMessageText { get; set; } = "";

        public List<string> AttachmentMimeTypes { get; set; } = [];
        public List<ChatAttachment> Attachments { get; set; } = [];
    }
}