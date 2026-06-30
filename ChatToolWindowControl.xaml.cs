using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VSIXGoogleChat.Services;

namespace VSIXGoogleChat
{
    public partial class ChatToolWindowControl : UserControl
    {
        private const int MaxHistoryBlocks = 1000;
        private static string FULL_FAKE_COMMAND = FakeCommandsGenerator.GenerateFakeCommand();

        private static readonly Regex DotnetRunRegex = new(@"dotnet\s+run\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ReplyRegex     = new(@"^\[Reply:\s*""(.*?)""\]\s*(.*)$", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string _fakeCommand = string.Empty;

        private GoogleChatService? _chatService;
        private Process?           _realPowerShellProcess;
        private ChatOptions        _chatOptions;

        private bool _isFirstStart = true;

        private bool    _isStealthMode     = false;
        private bool    _isSilentMode      = false;
        private string? _lastActiveSpaceId = null;
        
        private static readonly FontFamily      TerminalFontFamily = new(new Uri("pack://application:,,,/VSIXGoogleChat;component/Fonts/CascadiaMono.ttf"), "./#Cascadia Mono");
        private static readonly SolidColorBrush TerminalForeground = new(Color.FromRgb(0xFA, 0xFA, 0xFA));
        private static readonly SolidColorBrush PARTNER_COLOR      = new(Color.FromRgb(0x00, 0xCC, 0x66));
        private static readonly SolidColorBrush MY_COLOR           = new(Color.FromRgb(0x00, 0x8B, 0x8B));
        private static readonly SolidColorBrush SYSTEM_COLOR       = new(Color.FromRgb(0x66, 0x66, 0xFF));
        private static readonly SolidColorBrush DOTNET_RUN_COLOR   = new(Color.FromRgb(0xF9, 0xF1, 0xA5));
        private static readonly SolidColorBrush TIME_COLOR         = new(Color.FromRgb(0x80, 0x80, 0x80));

        private const double TerminalFontSize = 12;
        
        public event Action<bool>? RequestWindowVisibility;

        private CancellationTokenSource? _pollingCts         = null;
        private readonly object          _pollingLock        = new();
        private Task?                    _pollingTask;
        private DateTime                 _lastMessageTime    = DateTime.MinValue;
        private bool                     _firstLoadCompleted = false;
        private readonly HashSet<string> _displayedMessageIds = new();

        private readonly DispatcherTimer _idleTimer = new();
        private const int IdleTimeoutMs = 30000; // 30 secs

        private string _realMultilineText = string.Empty;
        private const string MultilinePlaceholder = " […]";

        private string _savedInputBeforeStealth = string.Empty;

        private readonly List<string> _commandHistory   = [];
        private int                   _historyIndex     = -1;
        private string?               _tempCurrentInput = null;

        private readonly SoundPlayer _notificationSound;
        private readonly SoundPlayer _successSound;
        private readonly SoundPlayer _errorSound;

        private DispatcherTimer?           _mediaTimer;
        private bool                       _isSliderDragging        = false;
        private ChatAttachment?            _activeAudioAttachment;
        private string?                    _activeMediaLocalPath;
        private HashSet<string>            _listenedVoiceMessages   = [];
        private bool                       _autoplayOnOpen          = false;
        private List<ChatAttachment>       _previewAttachments      = [];
        private int                        _currentPreviewIndex     = 0;
        private double                     _currentSpeed            = 1.0;
        private Dictionary<string, double> _voiceMessageTimings     = new(StringComparer.OrdinalIgnoreCase);
        private bool                       _isMediaPanelCollapsed   = false;
        private List<ChatAttachment>       _sessionAudioAttachments = [];
        private string?                    _nextPageToken           = null;

        private bool    _isLoadingOlderMessages = false;
        private string? _replyTargetText        = null;
        private string? _replyTargetMessageId   = null;
        private string? _replyTargetThreadName  = null;

        private readonly Dictionary<string, string>   _baseSpaceNames          = [];
        private readonly Dictionary<string, DateTime> _lastMessageTimePerSpace = [];
        private readonly Dictionary<string, int>      _unreadCountPerSpace     = [];
        private int _otherSpacesPollCounter = 0;
        private DispatcherTimer? _blinkingTimer = null;
        private bool _isBlinkingState = false;

        private static readonly Dictionary<string, Brush> AnsiColorMap = new()
        {
            ["30"] = Brushes.Black,
            ["31"] = Brushes.Red,
            ["32"] = Brushes.Green,
            ["33"] = Brushes.Yellow,
            ["34"] = Brushes.Blue,
            ["35"] = Brushes.Magenta,
            ["36"] = Brushes.Cyan,
            ["37"] = Brushes.White,

            ["90"] = Brushes.Gray,
            ["91"] = Brushes.OrangeRed,
            ["92"] = Brushes.LimeGreen,
            ["93"] = Brushes.Gold,
            ["94"] = Brushes.DodgerBlue,
            ["95"] = Brushes.Orchid,
            ["96"] = Brushes.Turquoise,
            ["97"] = Brushes.WhiteSmoke,
        };

        private AsyncPackage? _package;

        private bool _suppressAutoScroll = false;

        static ChatToolWindowControl()
        {
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            TerminalForeground.Freeze();
            PARTNER_COLOR.Freeze();
            MY_COLOR.Freeze();
            SYSTEM_COLOR.Freeze();
            DOTNET_RUN_COLOR.Freeze();
            TIME_COLOR.Freeze();
        }

        public ChatToolWindowControl()
        {
            InitializeComponent();
            DataContext = this;
            HistoryRichTextBox.CacheMode = new BitmapCache();

            _chatOptions = new();

            _notificationSound = new(Properties.Resources.notification);
            _successSound      = new(Properties.Resources.success);
            _errorSound        = new(Properties.Resources.error);

            InitializeAudioPlayer();
            LoadListenedMessages();
            LoadVoiceMessageTimings();

            DataObject.AddPastingHandler(this, OnPaste);

            Loaded   += ChatToolWindowControl_Loaded;
            Unloaded += ChatToolWindowControl_Unloaded;
        }

        private void StartIdleTimer()
        {
            _idleTimer.Interval = TimeSpan.FromMilliseconds(IdleTimeoutMs);
            _idleTimer.Tick += OnIdleTimerTick;
            _idleTimer.Start();

            this.PreviewKeyDown   += ResetIdleTimer;
            this.PreviewMouseMove += ResetIdleTimer;
        }

        private void ResetIdleTimer(object? sender = null, EventArgs? e = null)
        {
            if (_idleTimer != null)
            {
                _idleTimer.Stop();
                _idleTimer.Start();
            }
        }

        private async void OnIdleTimerTick(object sender, EventArgs e)
        {
            if (_mediaTimer != null && _mediaTimer.IsEnabled)
            {
                ResetIdleTimer();
                return;
            }

            if (!_isStealthMode)
            {
                await ToggleStealthModeAsync();
            }
        }

        public void SetPackage(AsyncPackage package)
        {
            _package = package;
        }

        private async void ChatToolWindowControl_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ChatToolWindowControl_Loaded;

            StartIdleTimer();

            InputTextBox.Focus();

            await ClearAndSetFakeTerminalOutputAsync();

            var scrollViewer = FindVisualChild<ScrollViewer>(HistoryRichTextBox);
            scrollViewer?.ScrollChanged += ScrollViewer_ScrollChanged;

            try
            {
                if (_package is null)
                    throw new NullReferenceException("Package is null!");

                _chatOptions = (ChatOptions)_package.GetDialogPage(typeof(ChatOptions));
                _chatService = await GoogleChatService.CreateAsync(_chatOptions);

                // Fetch and populate the Google Chat spaces dropdown selector
                await RefreshSpacesSelectorAsync();

                if (_chatOptions.EnableNotifications)
                {
                    RefreshHistory();
                    _ = StartPollingMessagesAsync();
                }

                if (_isFirstStart)
                {
                    _isFirstStart = false;
                    if (!_isStealthMode)
                        await ToggleStealthModeAsync();
                }
            }
            catch (Exception ex)
            {
                await AppendSystemMessageAsync($"Initialization error: {ex.Message}");
                _chatService = null;
            }
        }

        private async void ChatToolWindowControl_Unloaded(object sender, RoutedEventArgs e)
        {
            SaveVoiceMessageTimings();

            if (!_chatOptions.EnableNotifications)
                await StopPollingMessagesAsync();

            if (!_isStealthMode)
                await ToggleStealthModeAsync();

            Loaded += ChatToolWindowControl_Loaded;
        }

        private void RefreshHistory()
        {
            _sessionAudioAttachments.Clear();
            lock (_pollingLock) { _lastMessageTime = DateTime.MinValue; }
            lock (_displayedMessageIds) { _displayedMessageIds.Clear(); }
            _firstLoadCompleted = false;
        }

        public async Task RefreshSpacesSelectorAsync()
        {
            if (_chatService == null || _chatOptions == null) return;

            var spaces = await _chatService.GetSpacesAsync();
            foreach (var space in spaces)
            {
                string nickname = _chatOptions.GetSpaceNickname(space.Id);
                string baseName = !string.IsNullOrEmpty(nickname) ? nickname : space.Name;
                _baseSpaceNames[space.Id] = baseName;

                if (!_lastMessageTimePerSpace.ContainsKey(space.Id))
                {
                    _lastMessageTimePerSpace[space.Id] = DateTime.UtcNow;
                }
                if (!_unreadCountPerSpace.ContainsKey(space.Id))
                {
                    _unreadCountPerSpace[space.Id] = 0;
                }
            }
            
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SpaceSelector.ItemsSource = spaces;
            var currentSpaceId = _chatService.GetCurrentSpace();
            
            if (string.IsNullOrEmpty(currentSpaceId) && spaces.Any())
            {
                currentSpaceId = spaces.First().Id;
                _chatService.SetCurrentSpace(currentSpaceId);
            }
            
            _lastActiveSpaceId = currentSpaceId;

            if (!string.IsNullOrEmpty(currentSpaceId))
            {
                _unreadCountPerSpace[currentSpaceId] = 0;
            }

            UpdateSpacesVisualIndicators();
            StopSpaceSelectorBlinkingIfNoUnreads();

            SpaceSelector.SelectedItem = spaces.FirstOrDefault(s => s.Id == currentSpaceId);
        }

        private string GetAttachmentPlaceholder(List<string> mimeTypes)
        {
            if (mimeTypes == null || mimeTypes.Count == 0)
                return "[Media]";

            string mime = mimeTypes.First();

            if (mime.StartsWith("image/"))
                return "[Photo]";
            if (mime.StartsWith("audio/"))
                return "[Audio]";
            if (mime.StartsWith("video/"))
                return "[Video]";

            return mime switch
            {
                // Office and document formats
                "application/pdf" => "[PDF]",
                "application/msword"            or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"   => "[Word]",
                "application/vnd.ms-excel"      or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"         => "[Excel]",
                "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "[PowerPoint]",
                // Compressed file archives
                "application/zip"               or "application/x-zip-compressed" or "application/x-rar-compressed" or "application/x-7z-compressed" => "[Archive]",
                // Source code and plain text files
                "text/plain"                    or "text/x-csharp"                or "text/x-java"                  or "text/x-python" or "application/json" or "application/xml" or "text/xml" => "[Code]",
                // Unrecognized file formats
                _ => "[File]",
            };
        }

        private async Task StartPollingMessagesAsync()
        {
            await StopPollingMessagesAsync();

            _pollingCts = new CancellationTokenSource();
            var token = _pollingCts.Token;

            _pollingTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        if (_chatService != null && ((!_isSilentMode && !_isStealthMode) || _chatOptions.EnableNotifications))
                        {
                            DateTime lastTime;
                            lock (_pollingLock) { lastTime = _lastMessageTime; }

                            List<ChatMessage> newMessages;
                            bool refreshInProgress = !_firstLoadCompleted && lastTime == DateTime.MinValue;
                            if (refreshInProgress)
                            {
                                var (Messages, NextPageToken) = await _chatService.GetMessagesPageAsync(null, 30);
                                newMessages = Messages;
                                _nextPageToken = NextPageToken;
                            }
                            else
                            {
                                newMessages = await _chatService.GetMessagesAsync(lastTime);
                            }
                             if (newMessages.Any())
                             {
                                 bool allMyMessages = newMessages.All(m => m.SenderId == _chatOptions.MyChatUsername || m.SenderName == _chatOptions.MyChatUsername);

                                 if (_firstLoadCompleted && _chatOptions.EnableNotifications && !allMyMessages)
                                 {
                                     await Application.Current.Dispatcher.InvokeAsync(() => _notificationSound.Play());
                                 }

                                 var messagesToProcess = newMessages;
                                 if (refreshInProgress)
                                 {
                                     _firstLoadCompleted = true;
                                     _suppressAutoScroll = true;
                                 }

                                 foreach (var msg in messagesToProcess)
                                 {
                                     if (token.IsCancellationRequested) break;

                                     if (!string.IsNullOrEmpty(msg.Id))
                                     {
                                         lock (_displayedMessageIds)
                                         {
                                             if (_displayedMessageIds.Contains(msg.Id))
                                                 continue;
                                             _displayedMessageIds.Add(msg.Id);
                                         }
                                     }

                                     bool isOwnMessage = !string.IsNullOrEmpty(_chatOptions.MyChatUsername) && 
                                                         (msg.SenderId == _chatOptions.MyChatUsername || msg.SenderName == _chatOptions.MyChatUsername);

                                     var color = isOwnMessage ? MY_COLOR : PARTNER_COLOR;

                                     if (!_isSilentMode && !_isStealthMode)
                                     {
                                         bool hasAttachments = msg.Attachments != null && msg.Attachments.Any();
                                         if (refreshInProgress)
                                         {
                                             _suppressAutoScroll = true;
                                             await AppendMessageAsync(msg.SenderName, msg.Text, color, msg.CreateTime, msg.Attachments, msg.QuotedMessageText);
                                             _suppressAutoScroll = false;
                                         }
                                         else if (!isOwnMessage || hasAttachments)
                                         {
                                             await AppendMessageAsync(msg.SenderName, msg.Text, color, msg.CreateTime, msg.Attachments, msg.QuotedMessageText);
                                         }
                                     }
                                     
                                     lock (_pollingLock)
                                     {
                                         if (msg.CreateTime > _lastMessageTime)
                                         {
                                             _lastMessageTime = msg.CreateTime;
                                             string currentSpaceId = _chatService?.GetCurrentSpace() ?? "";
                                             if (!string.IsNullOrEmpty(currentSpaceId))
                                             {
                                                 _lastMessageTimePerSpace[currentSpaceId] = _lastMessageTime;
                                             }
                                         }
                                     }
                                 }

                                if (refreshInProgress && !_isSilentMode && !_isStealthMode)
                                {
                                    _suppressAutoScroll = false;
                                    ScrollToEnd();
                                }
                            }

                            _otherSpacesPollCounter++;
                            if (_otherSpacesPollCounter >= 4)
                            {
                                _otherSpacesPollCounter = 0;
                                _ = Task.Run(async () => await PollOtherSpacesAsync(token));
                            }
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { Debug.WriteLine($"Polling error: {ex.Message}"); }

                    try
                    {
                        const int timeToWaitMs = 500;
                        bool startedFromBackground = _isSilentMode || _isStealthMode;
                        int chunks = startedFromBackground ? 120 : 10;
                        for (int i = 0; i < chunks; i++)
                        {
                            if (!_isSilentMode && !_isStealthMode && startedFromBackground)
                                break;
                            await Task.Delay(timeToWaitMs, token);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                }
            }, token);
        }

        private async Task StopPollingMessagesAsync()
        {
            var taskToWait = _pollingTask;

            if (_pollingCts != null)
            {
                _pollingCts.Cancel();
                _pollingCts.Dispose();
                _pollingCts = null;
            }
            _pollingTask = null;

            // Await the old task on a background thread to prevent UI thread deadlock
            if (taskToWait != null)
            {
                _ = Task.Run(async () =>
                {
                    try { await taskToWait.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"StopPolling wait error: {ex.Message}"); }
                });
            }
            await Task.CompletedTask;
        }




        private Run CreateRun(string text)
        {
            return new Run(text)
            {
                FontFamily = TerminalFontFamily,
                FontSize   = TerminalFontSize,
                Foreground = TerminalForeground
            };
        }

        private void AddParagraphAndScroll(Paragraph paragraph)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(HistoryRichTextBox);
            bool wasAtBottom = true;
            if (scrollViewer != null)
            {
                wasAtBottom = scrollViewer.ScrollableHeight <= 0 ||
                              (scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset) <= 30;
            }

            HistoryRichTextBox.Document.Blocks.Add(paragraph);

            // Restrict maximum scrollback history by removing older message paragraphs
            while (HistoryRichTextBox.Document.Blocks.Count > MaxHistoryBlocks)
                HistoryRichTextBox.Document.Blocks.Remove(HistoryRichTextBox.Document.Blocks.FirstBlock);

            if (!_suppressAutoScroll && wasAtBottom)
                ScrollToEnd();
        }

        public void ScrollToEnd()
        {
            if (HistoryRichTextBox.Document == null) return;

            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                var scrollViewer = FindVisualChild<ScrollViewer>(HistoryRichTextBox);
                scrollViewer?.ScrollToEnd();
            }), DispatcherPriority.Loaded);

            InputTextBox.Focus();
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;
                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }
            return null;
        }


        public async Task ToggleStealthModeAsync(bool isManual = false)
        {
            if (_isSilentMode) await ToggleSilentModeAsync(isManual);

            _isStealthMode = !_isStealthMode;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isStealthMode)
            {
                CollapseMediaPanel();
                _replyTargetText = null;
                _replyTargetMessageId = null;
                _replyTargetThreadName = null;
                ReplyIndicatorBorder.Visibility = Visibility.Collapsed;

                SpaceSelector.Visibility = Visibility.Collapsed;
                _savedInputBeforeStealth = InputTextBox.Text;
                InputTextBox.Clear();

                if (_chatOptions.FakeTerminalOutput)
                    await ClearAndSetFakeTerminalOutputAsync();
                else
                    await ClearAndSetRealTerminalOutputAsync();

                if (_chatOptions.HideWindowStealthMode)
                    RequestWindowVisibility?.Invoke(false);
            }
            else
            {
                ExpandMediaPanel();
                SpaceSelector.Visibility = Visibility.Visible;
                ResetIdleTimer();

                if (!string.IsNullOrEmpty(_savedInputBeforeStealth))
                {
                    InputTextBox.Text        = _savedInputBeforeStealth;
                    InputTextBox.CaretIndex  = InputTextBox.Text.Length;
                    _savedInputBeforeStealth = string.Empty;
                }

                await ClearAndShowChatInterfaceAsync();
                RefreshHistory();
                _ = StartPollingMessagesAsync();
                RequestWindowVisibility?.Invoke(true);
            }
        }

        public async Task ToggleSilentModeAsync(bool isManual = false)
        {
            if (_isStealthMode) await ToggleStealthModeAsync(isManual);

            _isSilentMode = !_isSilentMode;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isSilentMode)
            {
                CollapseMediaPanel();
                _replyTargetText = null;
                _replyTargetMessageId = null;
                _replyTargetThreadName = null;
                ReplyIndicatorBorder.Visibility = Visibility.Collapsed;

                SpaceSelector.Visibility = Visibility.Collapsed;
                int caretPos = InputTextBox.CaretIndex;

                InputTextBox.Foreground = Brushes.Transparent;
                InputTextBox.CaretBrush = Brushes.Transparent;

                VisualTextBox.Visibility = Visibility.Visible;

                ClearRichTextBox(HistoryRichTextBox);
                UpdateVisualTextBox();

                VisualTextBox.CaretIndex = caretPos;
                ScrollTextBoxToCaret(VisualTextBox, caretPos);

                await AppendSystemMessageAsync("#compile.sma \"Build succeeded\"");
            }
            else
            {
                ExpandMediaPanel();
                ResetIdleTimer();

                ClearRichTextBox(HistoryRichTextBox);
                RefreshHistory();

                SpaceSelector.Visibility = Visibility.Visible;
                InputTextBox.Foreground = (Brush)FindResource("TerminalForeground");
                InputTextBox.CaretBrush = (Brush)FindResource("TerminalForeground");
                VisualTextBox.Visibility = Visibility.Collapsed;

                await AppendSystemMessageAsync("#compile.smd \"Build succeeded\"");
                _ = StartPollingMessagesAsync();
            }
        }

        public async Task ToggleNotificationsAsync(bool enable)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _chatOptions.EnableNotifications = enable;
            if (enable)
            {
                _ = StartPollingMessagesAsync();
                AppendSystemMessageAsync("Notifications enabled, background polling started.").FireAndForget();
            }
            else
            {
                if (_isSilentMode || _isStealthMode)
                {
                    await StopPollingMessagesAsync();
                    AppendSystemMessageAsync("Notifications disabled, background polling stopped.").FireAndForget();
                }
                else
                {
                    AppendSystemMessageAsync("Notifications disabled, background polling will be stopped.").FireAndForget();
                }
            }
        }

        private void StartSpaceSelectorBlinking()
        {
            if (_blinkingTimer != null) return;

            _blinkingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _blinkingTimer.Tick += BlinkingTimer_Tick;
            _blinkingTimer.Start();
        }

        private void StopSpaceSelectorBlinking()
        {
            if (_blinkingTimer == null) return;

            _blinkingTimer.Stop();
            _blinkingTimer = null;
            
            // Restore default color
            SpaceSelector.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x82, 0x6B));
        }

        private void BlinkingTimer_Tick(object? sender, EventArgs e)
        {
            _isBlinkingState = !_isBlinkingState;
            if (_isBlinkingState)
            {
                SpaceSelector.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66));
            }
            else
            {
                SpaceSelector.Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x82, 0x6B));
            }
        }

        private void StopSpaceSelectorBlinkingIfNoUnreads()
        {
            bool hasAnyUnread = _unreadCountPerSpace.Values.Any(count => count > 0);
            if (!hasAnyUnread)
            {
                StopSpaceSelectorBlinking();
            }
        }

        private void UpdateSpacesVisualIndicators()
        {
            if (SpaceSelector.ItemsSource == null) return;

            if (SpaceSelector.ItemsSource is not IEnumerable<ChatSpace> spaces) return;

            string currentSpaceId = _chatService?.GetCurrentSpace() ?? "";

            foreach (var space in spaces)
            {
                if (_baseSpaceNames.TryGetValue(space.Id, out string baseName))
                {
                    int unread = _unreadCountPerSpace.TryGetValue(space.Id, out int val) ? val : 0;
                    if (unread > 0 && space.Id != currentSpaceId)
                    {
                        space.Name = $"● {baseName} ({unread})";
                    }
                    else
                    {
                        space.Name = baseName;
                    }
                }
            }

            SpaceSelector.Items.Refresh();
        }

        private async Task PollOtherSpacesAsync(CancellationToken token)
        {
            if (_chatService == null) return;

            // 1. Fetch latest spaces from the server dynamically!
            var spaces = await _chatService.GetSpacesAsync();
            if (spaces == null || !spaces.Any()) return;

            // 2. Process on UI thread to update collections and items source if needed
            List<ChatSpace> updatedSpaces = [];
            string currentSpaceId = "";
            bool needsItemsSourceUpdate = false;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                currentSpaceId = _chatService.GetCurrentSpace();

                // Get current spaces list
                var currentSpaces = SpaceSelector.ItemsSource as IEnumerable<ChatSpace>;
                var currentIds = currentSpaces?.Select(s => s.Id).ToHashSet() ?? [];

                foreach (var space in spaces)
                {
                    // If this is a new space, mark that we need to update ComboBox items source
                    if (!currentIds.Contains(space.Id))
                    {
                        needsItemsSourceUpdate = true;
                    }

                    // Apply nickname if configured
                    string nickname = _chatOptions?.GetSpaceNickname(space.Id) ?? "";
                    string baseName = !string.IsNullOrEmpty(nickname) ? nickname : space.Name;
                    _baseSpaceNames[space.Id] = baseName;

                    if (!_lastMessageTimePerSpace.ContainsKey(space.Id))
                    {
                        _lastMessageTimePerSpace[space.Id] = DateTime.UtcNow;
                    }
                    if (!_unreadCountPerSpace.ContainsKey(space.Id))
                    {
                        _unreadCountPerSpace[space.Id] = 0;
                    }

                    // Set dynamic name with unread counts
                    int unread = _unreadCountPerSpace.TryGetValue(space.Id, out int val) ? val : 0;
                    if (unread > 0 && space.Id != currentSpaceId)
                    {
                        space.Name = $"● {baseName} ({unread})";
                    }
                    else
                    {
                        space.Name = baseName;
                    }

                    updatedSpaces.Add(space);
                }

                // If a space was removed from server, we also need to update ItemsSource
                if (currentSpaces != null && currentSpaces.Count() != updatedSpaces.Count)
                {
                    needsItemsSourceUpdate = true;
                }

                if (needsItemsSourceUpdate && !token.IsCancellationRequested)
                {
                    SpaceSelector.ItemsSource = updatedSpaces;
                    if (SpaceSelector.SelectedItem is ChatSpace selectedSpace)
                    {
                        SpaceSelector.SelectedItem = updatedSpaces.FirstOrDefault(s => s.Id == selectedSpace.Id);
                    }
                }
                else
                {
                    // If no new/removed spaces, just update the names of the existing items and refresh
                    if (currentSpaces != null)
                    {
                        foreach (var currentSpace in currentSpaces)
                        {
                            var matched = updatedSpaces.FirstOrDefault(s => s.Id == currentSpace.Id);
                            if (matched != null)
                            {
                                currentSpace.Name = matched.Name;
                            }
                        }
                        SpaceSelector.Items.Refresh();
                    }
                }
            });

            // 3. Poll each space for new messages
            foreach (var space in updatedSpaces)
            {
                if (token.IsCancellationRequested) break;
                if (space.Id == currentSpaceId) continue;

                if (_lastMessageTimePerSpace.TryGetValue(space.Id, out DateTime lastTime))
                {
                    var newMsgs = await _chatService.GetMessagesForSpaceAsync(space.Id, lastTime);
                    if (newMsgs != null && newMsgs.Any())
                    {
                        var newestMsg = newMsgs.OrderByDescending(m => m.CreateTime).First();
                        _lastMessageTimePerSpace[space.Id] = newestMsg.CreateTime;

                        var otherSenderMsgs = newMsgs.Where(m => m.SenderName != _chatOptions?.MyChatUsername).ToList();
                        if (otherSenderMsgs.Any())
                        {
                            int currentVal = _unreadCountPerSpace.TryGetValue(space.Id, out int val) ? val : 0;
                            _unreadCountPerSpace[space.Id] = currentVal + otherSenderMsgs.Count;

                            if (_chatOptions != null && _chatOptions.EnableNotifications)
                            {
                                await Application.Current.Dispatcher.InvokeAsync(() => _notificationSound.Play());
                            }

                            await Application.Current.Dispatcher.InvokeAsync(() =>
                            {
                                // Apply unread formatting to the space name
                                if (_baseSpaceNames.TryGetValue(space.Id, out string baseName))
                                {
                                    space.Name = $"● {baseName} ({_unreadCountPerSpace[space.Id]})";
                                    SpaceSelector.Items.Refresh();
                                }
                                StartSpaceSelectorBlinking();
                            });
                        }
                    }
                }
            }
        }
    }
}
