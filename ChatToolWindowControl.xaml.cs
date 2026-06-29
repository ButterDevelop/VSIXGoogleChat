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
        private const int MaxHistoryBlocks = 100;
        private static string FULL_FAKE_COMMAND = FakeCommandsGenerator.GenerateFakeCommand();

        private static readonly Regex DotnetRunRegex = new(@"dotnet\s+run\s+\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private string _fakeCommand = string.Empty;

        private GoogleChatService? _chatService;
        private Process?           _realPowerShellProcess;
        private ChatOptions        _chatOptions;

        private bool _isFirstStart  = true;

        private bool _isStealthMode = false;
        private bool _isSilentMode  = false;
        
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

        private readonly DispatcherTimer _idleTimer = new();
        private const int IdleTimeoutMs = 30000; // 30 secs

        private string _realMultilineText = string.Empty;
        private const string MultilinePlaceholder = " […]";

        private string _savedInputBeforeStealth = string.Empty;

        private readonly List<string> _commandHistory = [];
        private int                   _historyIndex     = -1;
        private string?               _tempCurrentInput = null;

        private readonly SoundPlayer _notificationSound;
        private readonly SoundPlayer _successSound;
        private readonly SoundPlayer _errorSound;

        private DispatcherTimer?     _mediaTimer;
        private bool                 _isSliderDragging = false;
        private ChatAttachment?      _activeAudioAttachment;
        private string?              _activeMediaLocalPath;
        private HashSet<string>      _listenedVoiceMessages = [];
        private bool                 _autoplayOnOpen = false;
        private List<ChatAttachment> _previewAttachments = [];
        private int                  _currentPreviewIndex = 0;

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
        }

        public ChatToolWindowControl()
        {
            InitializeComponent();
            DataContext = this;
            HistoryRichTextBox.CacheMode = new BitmapCache();

            _chatOptions = new();

            _notificationSound = new(Properties.Resources.notification);
            _successSound = new(Properties.Resources.success);
            _errorSound = new(Properties.Resources.error);

            InitializeAudioPlayer();
            LoadListenedMessages();

            DataObject.AddPastingHandler(this, OnPaste);

            Loaded += ChatToolWindowControl_Loaded;
            Unloaded += ChatToolWindowControl_Unloaded;
        }

        private void StartIdleTimer()
        {
            _idleTimer.Interval = TimeSpan.FromMilliseconds(IdleTimeoutMs);
            _idleTimer.Tick += OnIdleTimerTick;
            _idleTimer.Start();

            this.PreviewKeyDown += ResetIdleTimer;
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

            try
            {
                if (_package is null)
                    throw new NullReferenceException("Package is null!");

                _chatOptions = (ChatOptions)_package.GetDialogPage(typeof(ChatOptions));
                _chatService = await GoogleChatService.CreateAsync(_chatOptions);

                // Fetch and populate the Google Chat spaces dropdown selector
                var spaces = await _chatService.GetSpacesAsync();
                SpaceSelector.ItemsSource = spaces;
                var currentSpaceId = _chatService.GetCurrentSpace();
                SpaceSelector.SelectedItem = spaces.FirstOrDefault(s => s.Id == currentSpaceId);

                if (_chatOptions.EnableNotifications)
                {
                    RefreshHistory();
                    StartPollingMessages();
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
            if (!_chatOptions.EnableNotifications)
                await StopPollingMessagesAsync();

            if (!_isStealthMode)
                await ToggleStealthModeAsync();

            Loaded += ChatToolWindowControl_Loaded;
        }

        private void RefreshHistory()
        {
            lock (_pollingLock) { _lastMessageTime = DateTime.MinValue; }
            _firstLoadCompleted = false;
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
                "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "[Word]",
                "application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "[Excel]",
                "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "[PowerPoint]",
                // Compressed file archives
                "application/zip" or "application/x-zip-compressed" or "application/x-rar-compressed" or "application/x-7z-compressed" => "[Archive]",
                // Source code and plain text files
                "text/plain" or "text/x-csharp" or "text/x-java" or "text/x-python" or "application/json" or "application/xml" or "text/xml" => "[Code]",
                // Unrecognized file formats
                _ => "[File]",
            };
        }

        private async Task StartPollingMessages()
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

                            var newMessages = await _chatService.GetMessagesAsync(lastTime);

                            if (newMessages.Any())
                            {
                                bool allMyMessages = newMessages.All(m => m.SenderName == _chatOptions.MyChatUsername);

                                if (_firstLoadCompleted && _chatOptions.EnableNotifications && !allMyMessages)
                                {
                                    await Application.Current.Dispatcher.InvokeAsync(() => _notificationSound.Play());
                                }

                                var messagesToProcess  = newMessages;
                                bool refreshInProgress = !_firstLoadCompleted && lastTime == DateTime.MinValue;
                                if (refreshInProgress)
                                {
                                    messagesToProcess = newMessages.Skip(Math.Max(0, newMessages.Count - 30)).ToList();
                                    _firstLoadCompleted = true;
                                    _suppressAutoScroll = true;
                                }

                                foreach (var msg in messagesToProcess)
                                {
                                    var color = string.IsNullOrEmpty(_chatOptions.MyChatUsername)
                                        ? TerminalForeground
                                        : (_chatOptions.MyChatUsername == msg.SenderName ? MY_COLOR : PARTNER_COLOR);

                                    if (!_isSilentMode && !_isStealthMode)
                                    {
                                        bool isOwnMessage = _chatOptions.MyChatUsername == msg.SenderName;
                                        bool hasAttachments = msg.Attachments != null && msg.Attachments.Any();
                                        if (refreshInProgress)
                                        {
                                            _suppressAutoScroll = true;
                                            await AppendMessageAsync(msg.Text, color, msg.CreateTime, msg.Attachments);
                                            _suppressAutoScroll = false;
                                        }
                                        else if (!isOwnMessage || hasAttachments)
                                        {
                                            await AppendMessageAsync(msg.Text, color, msg.CreateTime, msg.Attachments);
                                        }
                                    }

                                    lock (_pollingLock)
                                    {
                                        if (msg.CreateTime > _lastMessageTime)
                                            _lastMessageTime = msg.CreateTime;
                                    }
                                }

                                if (refreshInProgress && !_isSilentMode && !_isStealthMode)
                                {
                                    _suppressAutoScroll = false;
                                    ScrollToEnd();
                                }
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
            if (_pollingCts != null)
            {
                _pollingCts.Cancel();
                if (_pollingTask != null)
                {
                    try { await _pollingTask; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Debug.WriteLine($"StopPolling error: {ex.Message}"); }
                    _pollingTask = null;
                }
                _pollingCts.Dispose();
                _pollingCts = null;
            }
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

        private async Task AddParagraphAndScrollAsync(Paragraph paragraph)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

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
            if (isManual)
            {
                CloseMediaPanel(true);
            }

            if (_isSilentMode) await ToggleSilentModeAsync(isManual);

            _isStealthMode = !_isStealthMode;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isStealthMode)
            {
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
                StartPollingMessages();
                RequestWindowVisibility?.Invoke(true);
            }
        }

        public async Task ToggleSilentModeAsync(bool isManual = false)
        {
            if (isManual)
            {
                CloseMediaPanel(true);
            }

            if (_isStealthMode) await ToggleStealthModeAsync(isManual);

            _isSilentMode = !_isSilentMode;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_isSilentMode)
            {
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
                ResetIdleTimer();

                ClearRichTextBox(HistoryRichTextBox);
                RefreshHistory();

                SpaceSelector.Visibility = Visibility.Visible;
                InputTextBox.Foreground = (Brush)FindResource("TerminalForeground");
                InputTextBox.CaretBrush = (Brush)FindResource("TerminalForeground");
                VisualTextBox.Visibility = Visibility.Collapsed;

                await AppendSystemMessageAsync("#compile.smd \"Build succeeded\"");
                StartPollingMessages();
            }
        }

        public async Task ToggleNotificationsAsync(bool enable)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _chatOptions.EnableNotifications = enable;
            if (enable)
            {
                StartPollingMessages();
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

    }
}
