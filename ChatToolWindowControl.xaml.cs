using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            _successSound      = new(Properties.Resources.success);
            _errorSound        = new(Properties.Resources.error);

            InitializeAudioPlayer();
            LoadListenedMessages();

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
                // Docs
                "application/pdf" => "[PDF]",
                "application/msword" or "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "[Word]",
                "application/vnd.ms-excel" or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => "[Excel]",
                "application/vnd.ms-powerpoint" or "application/vnd.openxmlformats-officedocument.presentationml.presentation" => "[PowerPoint]",
                // Archive
                "application/zip" or "application/x-zip-compressed" or "application/x-rar-compressed" or "application/x-7z-compressed" => "[Archive]",
                // Code / text
                "text/plain" or "text/x-csharp" or "text/x-java" or "text/x-python" or "application/json" or "application/xml" or "text/xml" => "[Code]",
                // Unknown type
                _ => "[File]",
            };
        }

        private async void StartPollingMessages()
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
                                        if (!isOwnMessage || refreshInProgress)
                                        {
                                            // no auto scroll to prevent user confusion if hes reading something
                                            _suppressAutoScroll = true;
                                            await AppendMessageAsync(msg.Text, color, msg.CreateTime, msg.Attachments);
                                            _suppressAutoScroll = false;
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

        private async void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;

            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                string clipboardText = Clipboard.GetText();
                if (!string.IsNullOrEmpty(clipboardText))
                {
                    bool isMultiline = clipboardText.Contains("\r\n") || clipboardText.Contains("\n") || clipboardText.Contains("\r");
                    if (isMultiline)
                    {
                        _realMultilineText = clipboardText;
                        string firstLine = clipboardText.Split(["\r\n", "\n", "\r"], StringSplitOptions.None)[0];
                        tb.Text       = firstLine + MultilinePlaceholder;
                        tb.CaretIndex = tb.Text.Length;
                        e.Handled = true;
                        AppendSystemMessageAsync("Multiline text pasted. Press Enter to send the whole message.").FireAndForget();
                        return;
                    }
                }
                return;
            }

            if (e.Key == Key.Escape)
            {
                if (MediaPanel.Visibility == Visibility.Visible)
                {
                    CloseMediaPanel(false);
                    e.Handled = true;
                    return;
                }

                if (!string.IsNullOrEmpty(_realMultilineText))
                {
                    _realMultilineText = string.Empty;
                    InputTextBox.Text  = "";
                    AppendSystemMessageAsync("Multiline input cancelled.").FireAndForget();
                    e.Handled = true;
                    return;
                }
            }

            // Up + Down keys for commands history in Stealth mode
            if (_isStealthMode && _chatOptions != null && !_chatOptions.FakeTerminalOutput)
            {
                if (e.Key == Key.Up)
                {
                    if (_commandHistory.Count > 0)
                    {
                        if (_historyIndex == -1)
                        {
                            _tempCurrentInput = tb.Text;
                            _historyIndex     = _commandHistory.Count - 1;
                        }
                        else if (_historyIndex > 0)
                        {
                            _historyIndex--;
                        }
                        tb.Text       = _commandHistory[_historyIndex];
                        tb.CaretIndex = tb.Text.Length;
                        e.Handled = true;
                    }
                    return;
                }
                else if (e.Key == Key.Down)
                {
                    if (_historyIndex != -1)
                    {
                        if (_historyIndex < _commandHistory.Count - 1)
                        {
                            _historyIndex++;
                            tb.Text       = _commandHistory[_historyIndex];
                            tb.CaretIndex = tb.Text.Length;
                        }
                        else
                        {
                            tb.Text = _tempCurrentInput ?? "";
                            _historyIndex     = -1;
                            _tempCurrentInput = null;
                            tb.CaretIndex = tb.Text.Length;
                        }
                        e.Handled = true;
                    }
                    return;
                }
            }

            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                string userInput;
                if (!string.IsNullOrEmpty(_realMultilineText))
                {
                    userInput = _realMultilineText;
                    _realMultilineText = string.Empty;
                }
                else
                {
                    userInput = tb.Text.Trim();
                }
                if (string.IsNullOrEmpty(userInput)) return;

                if (userInput.Equals("cls",   StringComparison.OrdinalIgnoreCase) ||
                    userInput.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    ClearRichTextBox(HistoryRichTextBox);
                    tb.Clear();
                    InputTextBox.Focus();
                    return;
                }

                if (_isStealthMode && _chatOptions != null && !_chatOptions.FakeTerminalOutput)
                {
                    if (_realPowerShellProcess != null && !_realPowerShellProcess.HasExited && _realPowerShellProcess.StandardInput != null)
                    {
                        if (!string.IsNullOrWhiteSpace(userInput))
                        {
                            if (_commandHistory.Count == 0 || _commandHistory.Last() != userInput)
                                _commandHistory.Add(userInput);
                        }
                        _historyIndex     = -1;
                        _tempCurrentInput = null;

                        await _realPowerShellProcess.StandardInput.WriteLineAsync(userInput);
                        await _realPowerShellProcess.StandardInput.FlushAsync();
                    }
                    else
                    {
                        AppendSystemMessageAsync("PowerShell process not running.").FireAndForget();
                    }
                    tb.Clear();
                    return;
                }

                if (_isSilentMode)
                {
                    int index = FULL_FAKE_COMMAND.IndexOf(" &&", tb.Text.Length);
                    tb.Clear();
                    if (index != -1)
                        _fakeCommand = FULL_FAKE_COMMAND.Substring(0, index);
                    await AppendTextAsync($"PS {Environment.CurrentDirectory}> {_fakeCommand}");

                    FULL_FAKE_COMMAND = FakeCommandsGenerator.GenerateFakeCommand();

                    try
                    {
                        _ = SendMessageWithFeedbackAsync(userInput);
                    }
                    catch (Exception ex)
                    {
                        await AppendSystemMessageAsync(ex.Message);
                    }

                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await AppendMessageAsync(userInput, MY_COLOR, DateTime.UtcNow);
                tb.Clear();
                _ = SendMessageWithFeedbackAsync(userInput);
            }
        }

        private async Task<bool> SendMessageWithFeedbackAsync(string text)
        {
            if (_chatService == null)
            {
                if (_chatOptions.EnableNotifications)
                    await Application.Current.Dispatcher.InvokeAsync(() => _errorSound?.Play());
                return false;
            }

            try
            {
                string? messageId = await _chatService.SendMessageAsync(text);
                bool success = !string.IsNullOrEmpty(messageId);
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (_chatOptions.EnableNotifications)
                    {
                        if (success)
                            _successSound?.Play();
                        else
                            _errorSound?.Play();
                    }
                });
                return success;
            }
            catch (Exception)
            {
                if (_chatOptions.EnableNotifications)
                    await Application.Current.Dispatcher.InvokeAsync(() => _errorSound?.Play());
                return false;
            }
        }

        private void InputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_realMultilineText) && !InputTextBox.Text.EndsWith(MultilinePlaceholder))
            {
                _realMultilineText = string.Empty;
            }

            if (_isSilentMode)
            {
                int oldCaret = InputTextBox.CaretIndex;
                UpdateVisualTextBox();
                VisualTextBox.CaretIndex = oldCaret;
                ScrollTextBoxToCaret(VisualTextBox, oldCaret);
            }
        }

        private void InputTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isSilentMode && VisualTextBox.Visibility == Visibility.Visible)
            {
                VisualTextBox.CaretIndex = InputTextBox.CaretIndex;
                ScrollTextBoxToCaret(VisualTextBox, InputTextBox.CaretIndex);
            }
        }

        private void ScrollTextBoxToCaret(TextBox tb, int caretIndex)
        {
            if (tb == null || string.IsNullOrEmpty(tb.Text)) return;
            if (caretIndex < 0) caretIndex = 0;
            if (caretIndex > tb.Text.Length) caretIndex = tb.Text.Length;

            var formattedText = new FormattedText(
                tb.Text.Substring(0, caretIndex),
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);

            double offsetX = formattedText.Width;
            tb.ScrollToHorizontalOffset(offsetX);
        }

        private void UpdateVisualTextBox()
        {
            if (_isSilentMode)
            {
                int rawLength = InputTextBox.Text.Length;
                while (rawLength >= FULL_FAKE_COMMAND.Length)
                    FULL_FAKE_COMMAND = FULL_FAKE_COMMAND + " && " + FULL_FAKE_COMMAND;

                _fakeCommand = FULL_FAKE_COMMAND.Substring(0, rawLength);
                VisualTextBox.Text       = _fakeCommand;
                VisualTextBox.Visibility = Visibility.Visible;
            }
            else
            {
                VisualTextBox.Visibility = Visibility.Collapsed;
            }
        }

        private void PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!InputTextBox.IsKeyboardFocused)
                InputTextBox.Focus();
        }

        private void HistoryRichTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is FrameworkContentElement source)
            {
                if (source.Parent is Hyperlink hyperlink)
                {
                    if (hyperlink.Tag is ChatAttachment att)
                    {
                        e.Handled = true;
                        _ = OpenMediaPreviewAsync(new List<ChatAttachment> { att }, 0);
                    }
                    else if (hyperlink.Tag is List<ChatAttachment> atts && atts.Any())
                    {
                        e.Handled = true;
                        _ = OpenMediaPreviewAsync(atts, 0);
                    }
                }
            }
        }

        private void HistoryRichTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            InputTextBox.Focus();
        }

        private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var selection = HistoryRichTextBox.Selection;
            if (!selection.IsEmpty)
            {
                string text = selection.Text;
                Clipboard.SetText(text);
            }
        }

        private void Grid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && MediaPanel.Visibility == Visibility.Visible)
            {
                CloseMediaPanel(false);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                return;
            }

            if (e.OriginalSource == InputTextBox) return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

            InputTextBox.Focus();

            string text = GetCharFromKey(e.Key);
            if (!string.IsNullOrEmpty(text))
            {
                int caret = InputTextBox.CaretIndex;
                InputTextBox.Text       = InputTextBox.Text.Insert(caret, text);
                InputTextBox.CaretIndex = caret + 1;
                e.Handled = true;
            }
        }

        private string GetCharFromKey(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                bool isShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                string letter = key.ToString();
                return isShift ? letter.ToUpper() : letter.ToLower();
            }
            
            if (key >= Key.D0 && key <= Key.D9)
            {
                string digit = (key - Key.D0).ToString();
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    string[] shiftDigits = [")", "!", "@", "#", "$", "%", "^", "&", "*", "("];
                    return shiftDigits[key - Key.D0];
                }
                return digit;
            }

            return key switch
            {
                Key.Space            => " ",
                Key.OemPeriod        => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ">" : ".",
                Key.OemComma         => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "<" : ",",
                Key.OemQuestion      => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "/" : "?",
                Key.OemSemicolon     => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ":" : ";",
                Key.OemQuotes        => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "\"" : "'",
                Key.OemOpenBrackets  => "[",
                Key.OemCloseBrackets => "]",
                Key.OemBackslash     => "\\",
                Key.OemMinus         => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "_" : "-",
                Key.OemPlus          => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "+" : "=",
                _                    => string.Empty,
            };
        }


        #region Messages in terminal

        private Paragraph CreateColoredLine(string fullText, Brush defaultColor, Brush promptColor, Brush dotnetRunColor)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0) };

            int promptIndex = fullText.IndexOf("PS ", StringComparison.Ordinal);
            if (promptIndex >= 0 && promptIndex < 3)
            {
                if (promptIndex > 0)
                {
                    paragraph.Inlines.Add(new Run(fullText.Substring(0, promptIndex))
                    {
                        Foreground = defaultColor,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    });
                }

                paragraph.Inlines.Add(new Run("PS")
                {
                    Foreground = promptColor,
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize,
                    FontWeight = FontWeights.Bold
                });

                string rest = fullText.Substring(promptIndex + 2);
                AddDotnetRunColoredRuns(paragraph, rest, defaultColor, dotnetRunColor);
            }
            else
            {
                AddDotnetRunColoredRuns(paragraph, fullText, defaultColor, dotnetRunColor);
            }

            return paragraph;
        }

        private void AddDotnetRunColoredRuns(Paragraph paragraph, string text, Brush defaultColor, Brush dotnetRunColor)
        {
            Match match = DotnetRunRegex.Match(text);

            if (match.Success)
            {
                if (match.Index > 0)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(0, match.Index))
                    {
                        Foreground = defaultColor,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    });
                }

                paragraph.Inlines.Add(new Run(text.Substring(match.Index, match.Length))
                {
                    Foreground = dotnetRunColor,
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize
                });

                if (match.Index + match.Length < text.Length)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(match.Index + match.Length))
                    {
                        Foreground = defaultColor,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    });
                }
            }
            else
            {
                paragraph.Inlines.Add(new Run(text)
                {
                    Foreground = defaultColor,
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize
                });
            }
        }

        private void ClearRichTextBox(RichTextBox rtb)
        {
            rtb.Document.Blocks.Clear();
            rtb.Document.Blocks.Add(new Paragraph(new Run("")));
            rtb.CaretPosition = rtb.Document.ContentEnd;
        }

        public async Task AppendTextAsync(string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var run       = CreateRun(text);
            var paragraph = new Paragraph(run) { Margin = new Thickness(0) };
            await AddParagraphAndScrollAsync(paragraph);
        }

        public async Task AppendMessageAsync(string text, SolidColorBrush color, DateTime? createTime = null, List<ChatAttachment>? attachments = null)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string timeStr = "";
            if (createTime.HasValue)
            {
                DateTime localTime = createTime.Value.ToLocalTime();
                timeStr = $"[{localTime:dd.MM.yyyy HH:mm}] ";
            }

            string full = $"PS C:\\Users\\{Environment.UserName}\\projects> dotnet run {FakeFilesGenerator.GenerateFakeFile()}{Environment.NewLine}";
            var paragraph = CreateColoredLine(full, TerminalForeground, color, DOTNET_RUN_COLOR);

            if (!string.IsNullOrWhiteSpace(text))
            {
                paragraph.Inlines.Add(new Run(text.Trim() + " ")
                {
                    Foreground = TerminalForeground,
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize
                });
            }

            if (attachments != null && attachments.Any())
            {
                var images = attachments.Where(att => att.ContentType.StartsWith("image/")).ToList();
                var nonImages = attachments.Where(att => !att.ContentType.StartsWith("image/")).ToList();

                if (images.Any())
                {
                    string placeholder = images.Count == 1 ? "[Photo]" : "[Photos]";
                    string safeName = string.IsNullOrEmpty(images.First().Name) ? Guid.NewGuid().ToString() : images.First().Name;
                    var link = new Hyperlink(new Run(placeholder))
                    {
                        NavigateUri     = new Uri("http://attachment/" + Uri.EscapeDataString(safeName)),
                        TextDecorations = null,
                        Foreground      = new SolidColorBrush(Color.FromRgb(0x7A, 0x89, 0xC2)),
                        FontFamily      = TerminalFontFamily,
                        FontSize        = TerminalFontSize,
                        Cursor          = Cursors.Hand,
                        Tag             = images
                    };
                    link.Click += AttachmentLink_Click;
                    paragraph.Inlines.Add(link);
                    paragraph.Inlines.Add(new Run(" ")
                    {
                        Foreground = TerminalForeground,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    });
                }

                foreach (var att in nonImages)
                {
                    bool isAudio = att.ContentType.StartsWith("audio/") || att.ContentName.EndsWith(".m4a") || att.ContentName.EndsWith(".mp3") || att.ContentName.EndsWith(".wav") || att.ContentName.EndsWith(".ogg");

                    string placeholder;
                    if (isAudio)
                    {
                        bool isListened = IsVoiceMessageListened(att.Name);
                        string status = isListened ? "Listened" : "New";
                        bool isVoiceMessage = att.ContentName.IndexOf("voice_message", StringComparison.OrdinalIgnoreCase) >= 0;
                        placeholder = isVoiceMessage ? $"[Play-Voice -Status {status}]" : $"[Play-Audio -Name \"{att.ContentName}\" -Status {status}]";
                    }
                    else
                    {
                        placeholder = GetAttachmentPlaceholder([att.ContentType]);
                    }

                    string safeName = string.IsNullOrEmpty(att.Name) ? Guid.NewGuid().ToString() : att.Name;
                    var link = new Hyperlink(new Run(placeholder))
                    {
                        NavigateUri     = new Uri("http://attachment/" + Uri.EscapeDataString(safeName)),
                        TextDecorations = null,
                        Foreground      = isAudio ? new SolidColorBrush(Color.FromRgb(0x7C, 0xB3, 0x42)) : new SolidColorBrush(Color.FromRgb(0x7A, 0x89, 0xC2)),
                        FontFamily      = TerminalFontFamily,
                        FontSize        = TerminalFontSize,
                        Cursor          = Cursors.Hand,
                        Tag             = att
                    };
                    link.Click += AttachmentLink_Click;
                    paragraph.Inlines.Add(link);
                    paragraph.Inlines.Add(new Run(" ")
                    {
                        Foreground = TerminalForeground,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    });
                }
            }

            if (!string.IsNullOrEmpty(timeStr))
            {
                paragraph.Inlines.Add(new Run(timeStr)
                {
                    Foreground = TIME_COLOR,
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize
                });
            }

            await AddParagraphAndScrollAsync(paragraph);
        }

        public async Task AppendSystemMessageAsync(string text, bool useNewLine = true)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string full = $"PS C:\\Users\\{Environment.UserName}\\projects> dotnet run {FakeFilesGenerator.GenerateFakeFile()}{Environment.NewLine}{text}";
            var paragraph = CreateColoredLine(full, TerminalForeground, SYSTEM_COLOR, DOTNET_RUN_COLOR);
            await AddParagraphAndScrollAsync(paragraph);
            if (useNewLine)
            {
                var emptyParagraph = new Paragraph { Margin = new Thickness(0) };
                await AddParagraphAndScrollAsync(emptyParagraph);
            }
        }

        public async Task AppendColoredMessageAsync(string text, Brush color)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var run = CreateRun($"PS C:\\Users\\{Environment.NewLine}\\projects> dotnet run {FakeFilesGenerator.GenerateFakeFile()}{Environment.NewLine}{text}");
            run.Foreground = color;
            var paragraph = new Paragraph(run) { Margin = new Thickness(0) };
            await AddParagraphAndScrollAsync(paragraph);
        }

        #endregion


        #region Fake/Real terminal

        public async Task ClearAndSetFakeTerminalOutputAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ClearRichTextBox(HistoryRichTextBox);

            AppendTerminalOutput("Windows PowerShell");
            AppendTerminalOutput($"Copyright (C) Microsoft Corporation. All rights reserved.{Environment.NewLine}");
        }

        public async Task ClearAndShowChatInterfaceAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (_realPowerShellProcess != null && !_realPowerShellProcess.HasExited)
            {
                _realPowerShellProcess.Kill();
                _realPowerShellProcess.Dispose();
                _realPowerShellProcess = null;
            }

            ClearRichTextBox(HistoryRichTextBox);
            AppendTerminalOutput("Windows PowerShell");
            AppendTerminalOutput($"Copyright (C) Microsoft Corporation. All rights reserved.{Environment.NewLine}");
            await AppendSystemMessageAsync("Stealth mode deactivated. Back to chat.");
        }

        public void AppendTerminalOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                foreach (var (fragment, brush) in ParseAnsi(text))
                {
                    var run = new Run(fragment)
                    {
                        Foreground = brush,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    };
                    paragraph.Inlines.Add(run);
                }
                HistoryRichTextBox.Document.Blocks.Add(paragraph);
                ScrollToEnd();
            }).FireAndForget();
        }

        public async Task ClearAndSetRealTerminalOutputAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ClearRichTextBox(HistoryRichTextBox);

            if (_realPowerShellProcess == null || _realPowerShellProcess.HasExited)
            {
                string initialCommand = RealCommandsGenerator.GenerateRealCommand();

                _realPowerShellProcess                                  = new Process();
                _realPowerShellProcess.StartInfo.FileName               = "pwsh.exe";
                _realPowerShellProcess.StartInfo.Arguments              = $"-NoExit -Command \"$PSStyle.OutputRendering = 'ANSI'; [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; cd C:\\Users\\{Environment.UserName}\\projects; {initialCommand}\"";
                _realPowerShellProcess.StartInfo.UseShellExecute        = false;
                _realPowerShellProcess.StartInfo.RedirectStandardOutput = true;
                _realPowerShellProcess.StartInfo.RedirectStandardError  = true;
                _realPowerShellProcess.StartInfo.RedirectStandardInput  = true;
                _realPowerShellProcess.StartInfo.CreateNoWindow         = true;
                _realPowerShellProcess.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                _realPowerShellProcess.StartInfo.StandardErrorEncoding  = System.Text.Encoding.UTF8;

                _realPowerShellProcess.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        AppendTerminalOutput(args.Data);
                };
                _realPowerShellProcess.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                        AppendTerminalOutput(args.Data);
                };

                AppendTerminalOutput("Windows PowerShell");
                AppendTerminalOutput($"Copyright (C) Microsoft Corporation. All rights reserved.{Environment.NewLine}");
                AppendTerminalOutput($"PS C:\\Users\\{Environment.UserName}\\projects> {initialCommand}");

                _commandHistory.Clear();
                _historyIndex     = -1;
                _tempCurrentInput = null;

                _realPowerShellProcess.Start();
                _realPowerShellProcess.BeginOutputReadLine();
                _realPowerShellProcess.BeginErrorReadLine();
            }
        }

        private IEnumerable<(string Text, Brush Color)> ParseAnsi(string input)
        {
            var regex     = new Regex(@"\x1b\[([\d;]+)m");
            var matches   = regex.Matches(input);
            int lastIndex = 0;
            Brush currentBrush = TerminalForeground;

            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                    yield return (input.Substring(lastIndex, match.Index - lastIndex), currentBrush);

                string codes = match.Groups[1].Value;
                var    parts = codes.Split(';');
                for (int i = 0; i < parts.Length; i++)
                {
                    string code = parts[i];
                    if (code == "0")
                    {
                        currentBrush = TerminalForeground;
                        break;
                    }
                    else if (code == "1" || code == "4" || code == "7" || code == "22" || code == "24" || code == "27")
                    {
                        continue;
                    }
                    else if (code == "38")
                    {
                        if (i + 2 < parts.Length && parts[i + 1] == "5")
                        {
                            i += 2;
                            continue;
                        }
                    }
                    else if (AnsiColorMap.TryGetValue(code, out var brush))
                    {
                        currentBrush = brush;
                    }
                }
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < input.Length)
                yield return (input.Substring(lastIndex), currentBrush);
        }

        #endregion


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
            HistoryRichTextBox.Document.Blocks.Add(paragraph);

            // cut old blocks
            while (HistoryRichTextBox.Document.Blocks.Count > MaxHistoryBlocks)
                HistoryRichTextBox.Document.Blocks.Remove(HistoryRichTextBox.Document.Blocks.FirstBlock);

            if (!_suppressAutoScroll)
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
                ResetIdleTimer();

                if (!string.IsNullOrEmpty(_savedInputBeforeStealth))
                {
                    InputTextBox.Text        = _savedInputBeforeStealth;
                    InputTextBox.CaretIndex  = InputTextBox.Text.Length;
                    _savedInputBeforeStealth = string.Empty;
                }

                await ClearAndShowChatInterfaceAsync();
                RefreshHistory();
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

                RefreshHistory();

                InputTextBox.Foreground  = TerminalForeground;
                InputTextBox.CaretBrush  = TerminalForeground;
                VisualTextBox.Visibility = Visibility.Collapsed;

                await AppendSystemMessageAsync("#compile.smd \"Build succeeded\"");
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

        #region Voice and Media Preview Support

        private string GetListenedMessagesFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localAppData, "VSIXInternalPowerShell");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "listened_voice_messages.txt");
        }

        private void LoadListenedMessages()
        {
            try
            {
                string path = GetListenedMessagesFilePath();
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    _listenedVoiceMessages = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadListenedMessages error: {ex.Message}");
            }
        }

        private void SaveListenedMessages()
        {
            try
            {
                string path = GetListenedMessagesFilePath();
                File.WriteAllLines(path, _listenedVoiceMessages);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveListenedMessages error: {ex.Message}");
            }
        }

        private bool IsVoiceMessageListened(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return false;
            return _listenedVoiceMessages.Contains(resourceName);
        }

        private void MarkVoiceMessageListened(string resourceName, bool listened)
        {
            if (string.IsNullOrEmpty(resourceName)) return;
            if (listened)
            {
                if (_listenedVoiceMessages.Add(resourceName))
                {
                    SaveListenedMessages();
                    UpdateVoiceMessageLinksInDoc(resourceName, "Listened");
                }
            }
            else
            {
                if (_listenedVoiceMessages.Remove(resourceName))
                {
                    SaveListenedMessages();
                    UpdateVoiceMessageLinksInDoc(resourceName, "New");
                }
            }
        }

        private void UpdateVoiceMessageLinksInDoc(string resourceName, string status)
        {
            var targets = new List<(Run run, ChatAttachment att)>();
            foreach (var block in HistoryRichTextBox.Document.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    foreach (var inline in paragraph.Inlines)
                    {
                        if (inline is Hyperlink link && link.Tag is ChatAttachment att && att.Name == resourceName)
                        {
                            if (link.Inlines.FirstInline is Run run)
                            {
                                targets.Add((run, att));
                            }
                        }
                    }
                }
            }

            foreach (var target in targets)
            {
                bool isVoiceMessage = target.att.ContentName.IndexOf("voice_message", StringComparison.OrdinalIgnoreCase) >= 0;
                target.run.Text = isVoiceMessage ? $"[Play-Voice -Status {status}]" : $"[Play-Audio -Name \"{target.att.ContentName}\" -Status {status}]";
            }
        }

        private void InitializeAudioPlayer()
        {
            AudioPlayerElement.Volume = 1.0;
            AudioPlayerElement.MediaOpened += MediaPlayer_MediaOpened;
            AudioPlayerElement.MediaEnded  += MediaPlayer_MediaEnded;
            AudioPlayerElement.MediaFailed += MediaPlayer_MediaFailed;

            _mediaTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _mediaTimer.Tick += MediaTimer_Tick;
        }

        private void MediaPlayer_MediaOpened(object? sender, RoutedEventArgs e)
        {
            if (AudioPlayerElement.NaturalDuration.HasTimeSpan)
            {
                var duration = AudioPlayerElement.NaturalDuration.TimeSpan;
                AudioDurationTextBlock.Text = $"Duration: {duration:mm\\:ss}";
                TimeTotalTextBlock.Text     = $"{duration:mm\\:ss}";
                AudioProgressSlider.Maximum = duration.TotalSeconds;
            }
            else
            {
                AudioDurationTextBlock.Text = "Duration: Unknown";
                TimeTotalTextBlock.Text     = "--:--";
            }
            AudioProgressSlider.Value = 0;
            TimeElapsedTextBlock.Text = "00:00";

            if (_autoplayOnOpen)
            {
                _autoplayOnOpen = false;
                PlayAudio();
            }
        }

        private void MediaPlayer_MediaEnded(object? sender, RoutedEventArgs e)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                StopAudio();
                if (_activeAudioAttachment != null)
                {
                    MarkVoiceMessageListened(_activeAudioAttachment.Name, true);
                    ListenedCheckBox.IsChecked = true;

                    PlayNextVoiceMessage();
                }
            }));
        }

        private void PlayNextVoiceMessage()
        {
            if (_activeAudioAttachment == null) return;

            bool foundCurrent = false;
            ChatAttachment? nextAudioAttachment = null;

            foreach (var block in HistoryRichTextBox.Document.Blocks)
            {
                if (block is Paragraph paragraph)
                {
                    foreach (var inline in paragraph.Inlines)
                    {
                        if (inline is Hyperlink link && link.Tag is ChatAttachment att)
                        {
                            bool isAudio = att.ContentType.StartsWith("audio/") || 
                                           att.ContentName.EndsWith(".m4a") || 
                                           att.ContentName.EndsWith(".mp3") || 
                                           att.ContentName.EndsWith(".wav") || 
                                           att.ContentName.EndsWith(".ogg");

                            if (isAudio)
                            {
                                if (foundCurrent)
                                {
                                    nextAudioAttachment = att;
                                    break;
                                }

                                if (att.Name == _activeAudioAttachment.Name)
                                {
                                    foundCurrent = true;
                                }
                            }
                        }
                    }
                }
                if (nextAudioAttachment != null)
                    break;
            }

            if (nextAudioAttachment != null)
            {
                _ = OpenMediaPreviewAsync(nextAudioAttachment);
            }
        }

        private void MediaPlayer_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
        {
            AudioStatusTextBlock.Text = $"Playback failed: {e.ErrorException.Message}";
            StopAudio();
        }

        private void MediaTimer_Tick(object? sender, EventArgs e)
        {
            if (!_isSliderDragging && AudioPlayerElement.Position != null)
            {
                AudioProgressSlider.Value = AudioPlayerElement.Position.TotalSeconds;
                TimeElapsedTextBlock.Text = $"{AudioPlayerElement.Position:mm\\:ss}";
            }
        }

        private void PlayAudio()
        {
            AudioPlayerElement.Play();
            _mediaTimer?.Start();
            AudioStatusTextBlock.Text = "Playing Voice Message...";
        }

        private void PauseAudio()
        {
            AudioPlayerElement.Pause();
            _mediaTimer?.Stop();
            AudioStatusTextBlock.Text = "Paused";
        }

        private void StopAudio()
        {
            AudioPlayerElement.Stop();
            _mediaTimer?.Stop();
            AudioPlayerElement.Position = TimeSpan.Zero;
            AudioProgressSlider.Value = 0;
            TimeElapsedTextBlock.Text = "00:00";
            AudioStatusTextBlock.Text = "Stopped";
        }

        private void PlayAudio_Click(object sender, RoutedEventArgs e)
        {
            PlayAudio();
        }

        private void PauseAudio_Click(object sender, RoutedEventArgs e)
        {
            PauseAudio();
        }

        private void StopAudio_Click(object sender, RoutedEventArgs e)
        {
            StopAudio();
        }

        private void AudioSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = true;
        }

        private void AudioSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = false;
            AudioPlayerElement.Position = TimeSpan.FromSeconds(AudioProgressSlider.Value);
        }

        private void ListenedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_activeAudioAttachment != null)
            {
                MarkVoiceMessageListened(_activeAudioAttachment.Name, true);
            }
        }

        private void ListenedCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_activeAudioAttachment != null)
            {
                MarkVoiceMessageListened(_activeAudioAttachment.Name, false);
            }
        }

        private void CloseMediaPanel(bool pauseOnly = false)
        {
            if (pauseOnly)
                PauseAudio();
            else
            {
                StopAudio();
                AudioPlayerElement.Source = null; // Release file lock
            }
            MediaPanel.Visibility     = Visibility.Collapsed;
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            AudioPlayerElement?.Volume = e.NewValue;
        }

        private void CloseMediaPanel_Click(object sender, RoutedEventArgs e)
        {
            CloseMediaPanel(false);
        }

        private void ShowFilePreview(ChatAttachment att, string localPath, string? errorMessage = null)
        {
            FilePreviewGrid.Visibility = Visibility.Visible;
            FileNameTextBlock.Text     = att.ContentName;
            FileMimeTextBlock.Text     = errorMessage ?? $"File Type: {att.ContentType}";
        }

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeMediaLocalPath != null && File.Exists(_activeMediaLocalPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(_activeMediaLocalPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to open file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ImagePreviewBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (PreviewImage.Stretch == Stretch.Uniform)
            {
                PreviewImage.Stretch = Stretch.UniformToFill;
            }
            else
            {
                PreviewImage.Stretch = Stretch.Uniform;
            }
        }

        private async void AttachmentLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Hyperlink link || link.Tag is null) return;
            e.Handled = true;

            if (link.Tag is ChatAttachment att)
            {
                await OpenMediaPreviewAsync(new List<ChatAttachment> { att }, 0);
            }
            else if (link.Tag is List<ChatAttachment> atts && atts.Any())
            {
                await OpenMediaPreviewAsync(atts, 0);
            }
        }

        private Task OpenMediaPreviewAsync(ChatAttachment att)
        {
            return OpenMediaPreviewAsync(new List<ChatAttachment> { att }, 0);
        }

        private async Task OpenMediaPreviewAsync(List<ChatAttachment> atts, int initialIndex = 0)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _previewAttachments = atts ?? new List<ChatAttachment>();
            _currentPreviewIndex = initialIndex;

            if (!_previewAttachments.Any())
            {
                MediaPanel.Visibility = Visibility.Collapsed;
                return;
            }

            if (_currentPreviewIndex < 0) _currentPreviewIndex = 0;
            if (_currentPreviewIndex >= _previewAttachments.Count) _currentPreviewIndex = _previewAttachments.Count - 1;

            var att = _previewAttachments[_currentPreviewIndex];

            MediaPanel.Visibility     = Visibility.Visible;

            ImagePreviewContainer.Visibility  = Visibility.Collapsed;
            AudioPreviewGrid.Visibility    = Visibility.Collapsed;
            AudioControlsBorder.Visibility = Visibility.Collapsed;
            FilePreviewGrid.Visibility     = Visibility.Collapsed;

            MediaTitleTextBlock.Text = "LOADING...";

            StopAudio();

            string tempPath;
            try
            {
                if (_chatService != null)
                {
                    string extension = Path.GetExtension(att.ContentName);
                    if (string.IsNullOrEmpty(extension))
                    {
                        extension = GetExtensionFromMimeType(att.ContentType);
                    }
                    
                    string localDir = Path.Combine(Path.GetTempPath(), "VSIXInternalPowerShell");
                    if (!Directory.Exists(localDir))
                        Directory.CreateDirectory(localDir);

                    string nameWithoutExt = Path.GetFileNameWithoutExtension(att.ContentName);
                    if (string.IsNullOrEmpty(nameWithoutExt)) nameWithoutExt = "file";

                    string safeName = $"{Math.Abs(att.Name.GetHashCode())}_{nameWithoutExt}{extension}";
                    foreach (char c in Path.GetInvalidFileNameChars())
                        safeName = safeName.Replace(c, '_');

                    tempPath = Path.Combine(localDir, safeName);

                    if (!File.Exists(tempPath))
                    {
                        using var stream = await _chatService.DownloadAttachmentAsync(att.Name);
                        if (stream != null)
                        {
                            using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                            await stream.CopyToAsync(fileStream);
                        }
                        else
                        {
                            throw new Exception("Failed to download file from Google Chat.");
                        }
                    }
                }
                else
                {
                    tempPath = CreateMockFileForTesting(att);
                }
            }
            catch (Exception ex)
            {
                MediaTitleTextBlock.Text   = "DOWNLOAD ERROR";
                FileNameTextBlock.Text     = att.ContentName;
                FileMimeTextBlock.Text     = $"Error: {ex.Message}";
                FilePreviewGrid.Visibility = Visibility.Visible;
                return;
            }

            _activeMediaLocalPath    = tempPath;

            if (_previewAttachments.Count > 1)
            {
                MediaTitleTextBlock.Text = $"PHOTOS ({_currentPreviewIndex + 1}/{_previewAttachments.Count})";
            }
            else
            {
                MediaTitleTextBlock.Text = att.ContentName.ToUpper();
            }

            if (att.ContentType.StartsWith("image/"))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(tempPath);
                    bitmap.EndInit();

                    PreviewImage.Source = bitmap;
                    ImagePreviewContainer.Visibility = Visibility.Visible;

                    if (_previewAttachments.Count > 1)
                    {
                        ImageNavigationPanel.Visibility = Visibility.Visible;
                        ImageIndexTextBlock.Text = $"{_currentPreviewIndex + 1} of {_previewAttachments.Count}";
                    }
                    else
                    {
                        ImageNavigationPanel.Visibility = Visibility.Collapsed;
                    }
                }
                catch (Exception ex)
                {
                    ShowFilePreview(att, tempPath, $"Failed to load image: {ex.Message}");
                }
            }
            else if (att.ContentType.StartsWith("audio/") || att.ContentName.EndsWith(".m4a") || att.ContentName.EndsWith(".mp3") || att.ContentName.EndsWith(".wav") || att.ContentName.EndsWith(".ogg"))
            {
                _activeAudioAttachment = att;

                AudioPreviewGrid.Visibility    = Visibility.Visible;
                AudioControlsBorder.Visibility = Visibility.Visible;

                AudioStatusTextBlock.Text  = "Voice Message Loaded";
                ListenedCheckBox.IsChecked = IsVoiceMessageListened(att.Name);
                
                try
                {
                    _autoplayOnOpen = true;
                    AudioPlayerElement.Source = new Uri(tempPath, UriKind.Absolute);
                    MarkVoiceMessageListened(att.Name, true);
                    ListenedCheckBox.IsChecked = true;
                }
                catch (Exception ex)
                {
                    AudioStatusTextBlock.Text = $"Error opening audio: {ex.Message}";
                }
            }
            else
            {
                ShowFilePreview(att, tempPath);
            }
        }

        private async void PrevImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_previewAttachments == null || _previewAttachments.Count <= 1) return;
            int nextIndex = _currentPreviewIndex - 1;
            if (nextIndex < 0) nextIndex = _previewAttachments.Count - 1;
            await OpenMediaPreviewAsync(_previewAttachments, nextIndex);
        }

        private async void NextImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_previewAttachments == null || _previewAttachments.Count <= 1) return;
            int nextIndex = _currentPreviewIndex + 1;
            if (nextIndex >= _previewAttachments.Count) nextIndex = 0;
            await OpenMediaPreviewAsync(_previewAttachments, nextIndex);
        }

        private string GetExtensionFromMimeType(string mimeType)
        {
            return mimeType switch
            {
                "image/png"                   => ".png",
                "image/jpeg" or "image/jpg"   => ".jpg",
                "image/gif"                   => ".gif",
                "audio/mpeg" or "audio/mp3"   => ".mp3",
                "audio/m4a"                   => ".m4a",
                "audio/wav"  or "audio/x-wav" => ".wav",
                "audio/ogg"  or "audio/x-ogg" => ".ogg",
                "video/mp4"                   => ".mp4",
                "text/plain"                  => ".txt",
                "application/pdf"             => ".pdf",
                _                             => ".dat"
            };
        }

        private string CreateMockFileForTesting(ChatAttachment att)
        {
            string localDir = Path.Combine(Path.GetTempPath(), "VSIXInternalPowerShell");
            if (!Directory.Exists(localDir))
                Directory.CreateDirectory(localDir);

            string extension = Path.GetExtension(att.ContentName);
            bool isAudio = att.ContentType.StartsWith("audio/") || att.ContentName.EndsWith(".m4a") || att.ContentName.EndsWith(".mp3") || att.ContentName.EndsWith(".wav") || att.ContentName.EndsWith(".ogg");
            if (isAudio)
            {
                extension = ".wav";
            }
            string nameWithoutExt = Path.GetFileNameWithoutExtension(att.ContentName);
            string safeName = $"mock_{Math.Abs(att.Name.GetHashCode())}_{nameWithoutExt}{extension}";
            foreach (char c in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(c, '_');

            string path = Path.Combine(localDir, safeName);

            if (File.Exists(path))
                return path;

            if (att.ContentType.StartsWith("image/"))
            {
                try
                {
                    using var bmp = new System.Drawing.Bitmap(300, 300);
                    using var g   = System.Drawing.Graphics.FromImage(bmp);
                    g.Clear(System.Drawing.Color.DarkSlateGray);
                    using (var font = new System.Drawing.Font("Consolas", 14, System.Drawing.FontStyle.Bold))
                    using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.Chartreuse))
                    {
                        g.DrawString("MOCK IMAGE PREVIEW", font, brush, new System.Drawing.PointF(30, 100));
                        g.DrawString(att.ContentName, font, System.Drawing.Brushes.White, new System.Drawing.PointF(20, 150));
                    }
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                catch
                {
                    File.WriteAllBytes(path, []);
                }
            }
            else if (att.ContentType.StartsWith("audio/") || att.ContentName.EndsWith(".m4a") || att.ContentName.EndsWith(".mp3") || att.ContentName.EndsWith(".wav") || att.ContentName.EndsWith(".ogg"))
            {
                try
                {
                    byte[] wavData = CreateBeepWavBytes(800, 1500); // 800 Hz beep, 1.5 seconds long
                    File.WriteAllBytes(path, wavData);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to write mock WAV: {ex.Message}");
                    File.WriteAllBytes(path, []);
                }
            }
            else
            {
                File.WriteAllText(path, $"Mock content for file: {att.ContentName}\nMIME type: {att.ContentType}");
            }

            return path;
        }

        private static byte[] CreateBeepWavBytes(int frequency, int durationMs)
        {
            int sampleRate = 16000;
            short bitsPerSample = 16;
            short channels = 1;
            
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            short blockAlign = (short)(channels * bitsPerSample / 8);
            
            int numSamples    = sampleRate * durationMs / 1000;
            int dataChunkSize = numSamples * channels * bitsPerSample / 8;
            int fileSize      = 36 + dataChunkSize;

            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write("RIFF".ToCharArray());
            writer.Write(fileSize);
            writer.Write("WAVE".ToCharArray());

            writer.Write("fmt ".ToCharArray());
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);

            writer.Write("data".ToCharArray());
            writer.Write(dataChunkSize);

            double t = 0.0;
            double dt = 2.0 * Math.PI * frequency / sampleRate;
            for (int i = 0; i < numSamples; i++)
            {
                short sample = (short)(Math.Sin(t) * short.MaxValue * 0.5); // 50% volume
                writer.Write(sample);
                t += dt;
            }

            writer.Flush();
            return ms.ToArray();
        }

        #endregion
    }
}