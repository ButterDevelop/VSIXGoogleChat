using Google.Apis.Auth.OAuth2;
using Google.Apis.HangoutsChat.v1;
using Google.Apis.HangoutsChat.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using System;
using VSIXGoogleChat;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace VSIXGoogleChat.Services
{
    public class GoogleChatService : IChatService
    {
        private readonly HangoutsChatService _chatService;
        private string _spaceId;
        private readonly ChatOptions _options;

        private GoogleChatService(HangoutsChatService chatService, ChatOptions options)
        {
            _chatService = chatService;
            _options = options;
            _spaceId = options.SpaceId;

            if (!string.IsNullOrEmpty(_spaceId) && !_spaceId.StartsWith("spaces/"))
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
                ApplicationName = "VSIXGoogleChatExtension"
            });

            var service = new GoogleChatService(chatService, options);

            if (string.IsNullOrEmpty(options.MyChatUsername))
            {
                try
                {
                    string? accessToken = credential.Token?.AccessToken;
                    if (string.IsNullOrEmpty(accessToken))
                    {
                        accessToken = await credential.GetAccessTokenForRequestAsync();
                    }

                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        string? myUserId = await service.GetMyUserIdAsync(accessToken);
                        if (!string.IsNullOrEmpty(myUserId))
                        {
                            options.MyChatUsername = myUserId;
                            options.SaveSettingsToStorage();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to auto-resolve MyChatUsername: {ex.Message}");
                }
            }

            return service;
        }

        private async Task<string?> GetMyUserIdAsync(string accessToken)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                string url = $"https://www.googleapis.com/oauth2/v3/tokeninfo?access_token={accessToken}";
                var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var match = Regex.Match(json, @"""sub""\s*:\s*""([^""]+)""");
                    if (match.Success)
                    {
                        return "users/" + match.Groups[1].Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to query tokeninfo: {ex.Message}");
            }
            return null;
        }

        private static async Task<UserCredential> LoadOAuth2CredentialAsync(string clientSecretsPath)
        {
            if (string.IsNullOrEmpty(clientSecretsPath))
                throw new InvalidOperationException("The path to client_secrets.json is not set in the settings.");
            if (!File.Exists(clientSecretsPath))
                throw new FileNotFoundException($"The client_secrets.json file not found: {clientSecretsPath}");

            using var stream = new FileStream(clientSecretsPath, FileMode.Open, FileAccess.Read);
            var secrets = (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets;

            string[] scopes = [HangoutsChatService.Scope.ChatMessages, "https://www.googleapis.com/auth/chat.spaces.readonly"];
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
                var message = new Message { Text = text };
                var request = _chatService.Spaces.Messages.Create(message, _spaceId);
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
            request.OrderBy = "create_time DESC";
            var response = await request.ExecuteAsync();

            if (response.Messages == null || !response.Messages.Any())
                return [];

            var allMessages = response.Messages
                .Select(m => new ChatMessage
                {
                    Id = m.Name,
                    Text = m.Text,
                    SenderName = GetSenderName(m),
                    CreateTime = m.CreateTimeDateTimeOffset?.UtcDateTime ?? DateTime.MinValue,
                    HasAttachments = m.Attachment != null && m.Attachment.Any(),
                    AttachmentMimeTypes = m.Attachment?.Select(a => a.ContentType).ToList() ?? [],
                    Attachments = m.Attachment?.Select(a => new ChatAttachment
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
                // Format the resource URL for media download. 
                // The resourceName argument follows the pattern: "spaces/{space}/messages/{message}/attachments/{attachment}"
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

        public void SetCurrentSpace(string spaceId)
        {
            _spaceId = spaceId;
            if (!_spaceId.StartsWith("spaces/"))
                _spaceId = "spaces/" + _spaceId;
        }

        public string GetCurrentSpace() => _spaceId;

        public async Task<List<ChatSpace>> GetSpacesAsync()
        {
            try
            {
                var request = _chatService.Spaces.List();
                var response = await request.ExecuteAsync();

                if (response.Spaces == null || !response.Spaces.Any())
                    return new List<ChatSpace>();

                var spaces = new List<ChatSpace>();
                foreach (var s in response.Spaces)
                {
                    string name = s.DisplayName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        if (s.Type == "DIRECT_MESSAGE")
                        {
                            try
                            {
                                var membersRequest = _chatService.Spaces.Members.List(s.Name);
                                var membersResponse = await membersRequest.ExecuteAsync();
                                if (membersResponse.Memberships != null)
                                {
                                    var otherMember = membersResponse.Memberships.FirstOrDefault(m =>
                                        m.Member != null && m.Member.Type == "HUMAN" &&
                                        !string.Equals(m.Member.DisplayName, _options.MyChatUsername, StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(m.Member.Name, _options.MyChatUsername, StringComparison.OrdinalIgnoreCase));

                                    if (otherMember != null && !string.IsNullOrWhiteSpace(otherMember.Member.DisplayName))
                                    {
                                        name = otherMember.Member.DisplayName;
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(name)) name = s.Name;

                    spaces.Add(new ChatSpace
                    {
                        Id = s.Name,
                        Name = name
                    });
                }
                return spaces;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetSpaces error: {ex.Message}");
                return new List<ChatSpace>();
            }
        }

        public async Task<string?> SendMessageWithAttachmentAsync(string text, string filePath, string mimeType)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return await SendMessageAsync(text);

            try
            {
                string filename = Path.GetFileName(filePath);
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

                var uploadRequest = _chatService.Media.Upload(
                    new UploadAttachmentRequest { Filename = filename },
                    _spaceId,
                    stream,
                    mimeType
                );

                var progress = await uploadRequest.UploadAsync();
                if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
                {
                    throw new Exception($"File upload failed: {progress.Exception?.Message ?? progress.Status.ToString()}");
                }

                var responseBody = uploadRequest.ResponseBody;
                if (responseBody?.AttachmentDataRef == null)
                {
                    throw new Exception("File upload succeeded but response is empty.");
                }

                var message = new Message
                {
                    Text = text,
                    Attachment = new List<Attachment>
                    {
                        new Attachment
                        {
                            AttachmentDataRef = new AttachmentDataRef
                            {
                                AttachmentUploadToken = responseBody.AttachmentDataRef.AttachmentUploadToken
                            }
                        }
                    }
                };

                var request = _chatService.Spaces.Messages.Create(message, _spaceId);
                var response = await request.ExecuteAsync();
                return response.Name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SendMessageWithAttachment error: {ex.Message}");
                throw;
            }
        }

        public async Task<string?> SendMessageWithAttachmentsAsync(string text, List<string> filePaths)
        {
            if (filePaths == null || !filePaths.Any())
                return await SendMessageAsync(text);

            try
            {
                var attachmentsList = new List<Attachment>();

                foreach (var filePath in filePaths)
                {
                    if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                        continue;

                    string filename = Path.GetFileName(filePath);
                    string mimeType = GetMimeType(filename);
                    using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

                    var uploadRequest = _chatService.Media.Upload(
                        new UploadAttachmentRequest { Filename = filename },
                        _spaceId,
                        stream,
                        mimeType
                    );

                    var progress = await uploadRequest.UploadAsync();
                    if (progress.Status != Google.Apis.Upload.UploadStatus.Completed)
                    {
                        throw new Exception($"File upload failed for {filename}: {progress.Exception?.Message ?? progress.Status.ToString()}");
                    }

                    var responseBody = uploadRequest.ResponseBody;
                    if (responseBody?.AttachmentDataRef == null)
                    {
                        throw new Exception($"File upload succeeded for {filename} but response is empty.");
                    }

                    attachmentsList.Add(new Attachment
                    {
                        AttachmentDataRef = new AttachmentDataRef
                        {
                            AttachmentUploadToken = responseBody.AttachmentDataRef.AttachmentUploadToken
                        }
                    });
                }

                var message = new Message
                {
                    Text = text,
                    Attachment = attachmentsList
                };

                var request = _chatService.Spaces.Messages.Create(message, _spaceId);
                var response = await request.ExecuteAsync();
                return response.Name;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SendMessageWithAttachments error: {ex.Message}");
                throw;
            }
        }

        private string GetMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".zip" => "application/zip",
                ".mp3" => "audio/mpeg",
                ".wav" => "audio/wav",
                ".mp4" => "video/mp4",
                _ => "application/octet-stream"
            };
        }

        private string GetSenderName(Message message)
        {
            return message.Sender?.DisplayName ?? message.Sender?.Name ?? "Unknown";
        }
    }
}