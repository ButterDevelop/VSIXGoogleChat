using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VSIXGoogleChat.Services
{
    public interface IChatService
    {
        Task<string?> SendMessageAsync(string text);

        Task<List<ChatMessage>> GetMessagesAsync(DateTime? lastMessageTime = null, int maxCount = 50);

        Task<System.IO.Stream?> DownloadAttachmentAsync(string resourceName);

        Task<List<ChatSpace>> GetSpacesAsync();
        void SetCurrentSpace(string spaceId);
        string GetCurrentSpace();
        Task<string?> SendMessageWithAttachmentAsync(string text, string filePath, string mimeType);
        Task<string?> SendMessageWithAttachmentsAsync(string text, List<string> filePaths);
    }

    public class ChatSpace
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
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
        public DateTime CreateTime { get; set; }
        public bool HasAttachments { get; set; }

        public List<string> AttachmentMimeTypes { get; set; } = [];
        public List<ChatAttachment> Attachments { get; set; } = [];
    }
}