using Google.Apis.Auth.OAuth2;
using Google.Apis.HangoutsChat.v1;
using Google.Apis.HangoutsChat.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VSIXGoogleChat.Services
{
    public class GoogleChatService : IChatService
    {
        private readonly HangoutsChatService _chatService;
        private readonly string _spaceId;

        private GoogleChatService(HangoutsChatService chatService, string spaceId)
        {
            _chatService = chatService;
            _spaceId     = spaceId;

            if (!_spaceId.StartsWith("spaces/"))
                _spaceId = "spaces/" + _spaceId;
        }

        public static async Task<GoogleChatService> CreateAsync(ChatOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var credential = await LoadOAuth2CredentialAsync(options.GoogleCredentialsPath);
            var chatService = new HangoutsChatService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName       = "VSIXGoogleChatExtension"
            });

            return new GoogleChatService(chatService, options.SpaceId);
        }

        private static async Task<UserCredential> LoadOAuth2CredentialAsync(string clientSecretsPath)
        {
            if (string.IsNullOrEmpty(clientSecretsPath))
                throw new InvalidOperationException("The path to client_secrets.json is not set in the settings.");
            if (!File.Exists(clientSecretsPath))
                throw new FileNotFoundException($"The client_secrets.json file not found: {clientSecretsPath}");

            using var stream = new FileStream(clientSecretsPath, FileMode.Open, FileAccess.Read);
            var secrets = (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets;

            string[] scopes = [HangoutsChatService.Scope.ChatMessages];
            string localAppData   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tokenStorePath = Path.Combine(localAppData, "VSIXInternalPowerShell", "TokenStore");
            var dataStore = new FileDataStore(tokenStorePath, true);

            return await GoogleWebAuthorizationBroker.AuthorizeAsync(
                secrets,
                scopes,
                Environment.UserName,
                CancellationToken.None,
                dataStore
            );
        }

        public async Task<string?> SendMessageAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            try
            {
                var message  = new Message { Text = text };
                var request  = _chatService.Spaces.Messages.Create(message, _spaceId);
                var response = await request.ExecuteAsync();
                return response.Name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SendMessage error: {ex.Message}");
                return null;
            }
        }

        public async Task<List<ChatMessage>> GetMessagesAsync(DateTime? lastMessageTime = null, int maxCount = 50)
        {
            var request = _chatService.Spaces.Messages.List(_spaceId);
            request.PageSize = maxCount;
            request.OrderBy  = "create_time DESC";
            var response = await request.ExecuteAsync();

            if (response.Messages == null || !response.Messages.Any())
                return [];

            var allMessages = response.Messages
                .Select(m => new ChatMessage
                {
                    Id                  = m.Name,
                    Text                = m.Text,
                    SenderName          = GetSenderName(m),
                    CreateTime          = m.CreateTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue,
                    HasAttachments      = m.Attachment != null && m.Attachment.Any(),
                    AttachmentMimeTypes = m.Attachment?.Select(a => a.ContentType).ToList() ?? [],
                    Attachments         = m.Attachment?.Select(a => new ChatAttachment
                    {
                        Name = a.AttachmentDataRef?.ResourceName ?? a.Name ?? "",
                        ContentName = a.ContentName ?? "",
                        ContentType = a.ContentType ?? "",
                        ContentUri = a.ThumbnailUri ?? "",
                        MessageId = m.Name
                    }).ToList() ?? []
                })
                .OrderBy(m => m.CreateTime)
                .ToList();

            if (lastMessageTime.HasValue && lastMessageTime.Value > DateTime.MinValue)
            {
                return allMessages.Where(m => m.CreateTime > lastMessageTime.Value).ToList();
            }
            return allMessages;
        }

        public async Task<Stream?> DownloadAttachmentAsync(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName))
                return null;

            try
            {
                // Ensure resourceName is encoded or formatted correctly if it contains spaces or slashes
                // resourceName is typically like "spaces/space_id/messages/msg_id/attachments/att_id/paths"
                string url = $"https://chat.googleapis.com/v1/media/{resourceName}?alt=media";
                var response = await _chatService.HttpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStreamAsync();
                }
                else
                {
                    Debug.WriteLine($"DownloadAttachment failed: {response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DownloadAttachment error: {ex.Message}");
            }
            return null;
        }

        private string GetSenderName(Message message)
        {
            return message.Sender?.DisplayName ?? message.Sender?.Name ?? "Unknown";
        }
    }
}