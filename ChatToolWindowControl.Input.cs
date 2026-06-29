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
        private static readonly List<CommandSuggestion> AllSuggestions = new()
        {
            new CommandSuggestion { Command = "#file", Description = "Upload and send file(s). Example: #file C:\\pic.png" },
            new CommandSuggestion { Command = "#upload", Description = "Upload and send file(s). Example: #upload C:\\pic.png" },
            new CommandSuggestion { Command = "#setname", Description = "Assign a custom nickname to this chat space. Use without arguments to clear." },
            new CommandSuggestion { Command = "#clear", Description = "Clear the chat screen. (Alias: #cls)" },
            new CommandSuggestion { Command = "#status", Description = "Show current chat connection status, space details, and settings." },
            new CommandSuggestion { Command = "#stealth", Description = "Toggle Stealth Mode." },
            new CommandSuggestion { Command = "#silent", Description = "Toggle Silent Mode." },
            new CommandSuggestion { Command = "#mute", Description = "Toggle notification sounds on/off." },
            new CommandSuggestion { Command = "#spaces", Description = "List all available spaces and direct messages with their IDs." },
            new CommandSuggestion { Command = "#help", Description = "Display detailed help about all available chat commands. (Alias: #?)" }
        };

        private async void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox tb) return;

            if (SuggestionsPopup != null && SuggestionsPopup.IsOpen)
            {
                if (e.Key == Key.Up)
                {
                    e.Handled = true;
                    int index = SuggestionsListBox.SelectedIndex - 1;
                    if (index < 0) index = SuggestionsListBox.Items.Count - 1;
                    SuggestionsListBox.SelectedIndex = index;
                    SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
                    return;
                }
                if (e.Key == Key.Down)
                {
                    e.Handled = true;
                    int index = SuggestionsListBox.SelectedIndex + 1;
                    if (index >= SuggestionsListBox.Items.Count) index = 0;
                    SuggestionsListBox.SelectedIndex = index;
                    SuggestionsListBox.ScrollIntoView(SuggestionsListBox.SelectedItem);
                    return;
                }
                if (e.Key == Key.Enter || e.Key == Key.Tab)
                {
                    e.Handled = true;
                    if (SuggestionsListBox.SelectedItem is CommandSuggestion selected)
                    {
                        ApplySuggestion(selected);
                    }
                    else
                    {
                        SuggestionsPopup.IsOpen = false;
                    }
                    return;
                }
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    SuggestionsPopup.IsOpen = false;
                    return;
                }
            }

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
                        tb.Text = firstLine + MultilinePlaceholder;
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
                    InputTextBox.Text = "";
                    AppendSystemMessageAsync("Multiline input cancelled.").FireAndForget();
                    e.Handled = true;
                    return;
                }
            }

            // Handle command history navigation using Up/Down arrow keys in Stealth Mode (when running a real terminal)
            if (_isStealthMode && _chatOptions != null && !_chatOptions.FakeTerminalOutput)
            {
                if (e.Key == Key.Up)
                {
                    if (_commandHistory.Count > 0)
                    {
                        if (_historyIndex == -1)
                        {
                            _tempCurrentInput = tb.Text;
                            _historyIndex = _commandHistory.Count - 1;
                        }
                        else if (_historyIndex > 0)
                        {
                            _historyIndex--;
                        }
                        tb.Text = _commandHistory[_historyIndex];
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
                            tb.Text = _commandHistory[_historyIndex];
                            tb.CaretIndex = tb.Text.Length;
                        }
                        else
                        {
                            tb.Text = _tempCurrentInput ?? "";
                            _historyIndex = -1;
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

                if (userInput.StartsWith("#file ", StringComparison.OrdinalIgnoreCase) ||
                    userInput.StartsWith("#upload ", StringComparison.OrdinalIgnoreCase))
                {
                    string remainder = userInput.Substring(userInput.IndexOf(' ') + 1).Trim();
                    var parsed = ParseFilePathsAndMessage(remainder);
                    tb.Clear();
                    _ = UploadAndSendFilesAsync(parsed.Paths, parsed.Message);
                    return;
                }

                if (userInput.StartsWith("#setname ", StringComparison.OrdinalIgnoreCase) ||
                    userInput.Equals("#setname", StringComparison.OrdinalIgnoreCase))
                {
                    string nickname = "";
                    if (userInput.StartsWith("#setname ", StringComparison.OrdinalIgnoreCase))
                    {
                        nickname = userInput.Substring("#setname ".Length).Trim();
                    }

                    tb.Clear();

                    if (_chatService == null || _chatOptions == null)
                    {
                        await AppendSystemMessageAsync("Chat service is not initialized.");
                        return;
                    }

                    string currentSpaceId = _chatService.GetCurrentSpace();
                    if (string.IsNullOrEmpty(currentSpaceId))
                    {
                        await AppendSystemMessageAsync("No active space selected.");
                        return;
                    }

                    _chatOptions.SetSpaceNickname(currentSpaceId, nickname);
                    _chatOptions.SaveSettingsToStorage();

                    if (string.IsNullOrEmpty(nickname))
                    {
                        await AppendSystemMessageAsync("Space nickname cleared.");
                    }
                    else
                    {
                        await AppendSystemMessageAsync($"Space nickname set to: '{nickname}'.");
                    }

                    await RefreshSpacesSelectorAsync();
                    return;
                }

                if (userInput.Equals("#help", StringComparison.OrdinalIgnoreCase) ||
                    userInput.Equals("#?", StringComparison.OrdinalIgnoreCase))
                {
                    tb.Clear();
                    await AppendSystemMessageAsync("Available commands:\n" +
                        "  #file <path1>, <path2> [message] - Upload and send file(s).\n" +
                        "  #upload <path1>, <path2> [message] - Alias for #file.\n" +
                        "  #setname <nickname> - Set a custom nickname for this space (empty to clear).\n" +
                        "  #clear or #cls - Clear the chat screen.\n" +
                        "  #status - Show connection status, space details, and modes.\n" +
                        "  #stealth - Toggle Stealth Mode.\n" +
                        "  #silent - Toggle Silent Mode.\n" +
                        "  #mute - Toggle sound notifications on/off.\n" +
                        "  #spaces - List all available chat spaces with their IDs.\n" +
                        "  #help or #? - Show this help menu.", useNewLine: true);
                    return;
                }

                if (userInput.Equals("#clear", StringComparison.OrdinalIgnoreCase) ||
                    userInput.Equals("#cls", StringComparison.OrdinalIgnoreCase))
                {
                    ClearRichTextBox(HistoryRichTextBox);
                    tb.Clear();
                    InputTextBox.Focus();
                    return;
                }

                if (userInput.Equals("#status", StringComparison.OrdinalIgnoreCase))
                {
                    tb.Clear();
                    string spaceName = "Unknown";
                    string currentSpaceId = _chatService?.GetCurrentSpace() ?? "";
                    if (_chatService != null)
                    {
                        var spaces = await _chatService.GetSpacesAsync();
                        var space = spaces.FirstOrDefault(s => s.Id == currentSpaceId);
                        if (space != null)
                        {
                            spaceName = space.Name;
                        }
                    }

                    string nickname = _chatOptions != null && !string.IsNullOrEmpty(currentSpaceId) 
                        ? _chatOptions.GetSpaceNickname(currentSpaceId) 
                        : "";

                    string nicknameStr = string.IsNullOrEmpty(nickname) ? "None" : $"'{nickname}'";
                    string silentStr = _isSilentMode ? "Active" : "Inactive";
                    string stealthStr = _isStealthMode ? "Active" : "Inactive";
                    string notificationsStr = _chatOptions != null && _chatOptions.EnableNotifications ? "Enabled" : "Disabled";

                    await AppendSystemMessageAsync($"Chat Status:\n" +
                        $"  Active Space ID: {currentSpaceId}\n" +
                        $"  Active Space Name: {spaceName}\n" +
                        $"  Nickname: {nicknameStr}\n" +
                        $"  Stealth Mode: {stealthStr}\n" +
                        $"  Silent Mode: {silentStr}\n" +
                        $"  Notifications Sound: {notificationsStr}", useNewLine: true);
                    return;
                }

                if (userInput.Equals("#stealth", StringComparison.OrdinalIgnoreCase))
                {
                    tb.Clear();
                    await ToggleStealthModeAsync();
                    return;
                }

                if (userInput.Equals("#silent", StringComparison.OrdinalIgnoreCase))
                {
                    tb.Clear();
                    await ToggleSilentModeAsync();
                    return;
                }

                if (userInput.Equals("#mute", StringComparison.OrdinalIgnoreCase))
                {
                    tb.Clear();
                    if (_chatOptions != null)
                    {
                        _chatOptions.EnableNotifications = !_chatOptions.EnableNotifications;
                        _chatOptions.SaveSettingsToStorage();
                        string state = _chatOptions.EnableNotifications ? "enabled" : "disabled";
                        await AppendSystemMessageAsync($"Sound notifications are now {state}.");
                    }
                    return;
                }

                if (userInput.Equals("#spaces", StringComparison.OrdinalIgnoreCase))
                {
                    tb.Clear();
                    if (_chatService == null || _chatOptions == null)
                    {
                        await AppendSystemMessageAsync("Chat service not initialized.");
                        return;
                    }

                    var spacesList = await _chatService.GetSpacesAsync();
                    var sb = new System.Text.StringBuilder("Available Spaces:\n");
                    foreach (var s in spacesList)
                    {
                        string nickname = _chatOptions.GetSpaceNickname(s.Id);
                        string nicknamePart = string.IsNullOrEmpty(nickname) ? "" : $" (Nickname: '{nickname}')";
                        sb.AppendLine($"  - ID: {s.Id} | Name: {s.Name}{nicknamePart}");
                    }
                    await AppendSystemMessageAsync(sb.ToString().TrimEnd(), useNewLine: true);
                    return;
                }

                if (userInput.Equals("cls", StringComparison.OrdinalIgnoreCase) ||
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
                        _historyIndex = -1;
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

            if (InputTextBox == null || SuggestionsPopup == null || SuggestionsListBox == null) return;

            string text = InputTextBox.Text;
            if (text.StartsWith("#"))
            {
                string query = text.Substring(1).ToLowerInvariant();
                var filtered = AllSuggestions
                    .Where(s => s.Command.Substring(1).StartsWith(query))
                    .ToList();

                if (filtered.Any())
                {
                    SuggestionsListBox.ItemsSource = filtered;
                    if (SuggestionsListBox.SelectedIndex < 0 || SuggestionsListBox.SelectedIndex >= filtered.Count)
                    {
                        SuggestionsListBox.SelectedIndex = 0;
                    }
                    SuggestionsPopup.IsOpen = true;
                }
                else
                {
                    SuggestionsPopup.IsOpen = false;
                }
            }
            else
            {
                SuggestionsPopup.IsOpen = false;
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
                VisualTextBox.Text = _fakeCommand;
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
            if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                try
                {
                    var dataObject = Clipboard.GetDataObject();
                    if (dataObject != null)
                    {
                        if (dataObject.GetDataPresent(DataFormats.FileDrop))
                        {
                            var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                            if (files != null && files.Length > 0)
                            {
                                e.Handled = true;
                                _ = UploadAndSendFilesAsync(files.ToList());
                                return;
                            }
                        }
                        else if (dataObject.GetDataPresent(DataFormats.Bitmap))
                        {
                            var image = Clipboard.GetImage();
                            if (image != null)
                            {
                                e.Handled = true;
                                _ = UploadAndSendImageSourceAsync(image);
                                return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Clipboard paste check failed: {ex.Message}");
                }
            }

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
                InputTextBox.Text = InputTextBox.Text.Insert(caret, text);
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
                Key.Space => " ",
                Key.OemPeriod => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ">" : ".",
                Key.OemComma => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "<" : ",",
                Key.OemQuestion => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "/" : "?",
                Key.OemSemicolon => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? ":" : ";",
                Key.OemQuotes => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "\"" : "'",
                Key.OemOpenBrackets => "[",
                Key.OemCloseBrackets => "]",
                Key.OemBackslash => "\\",
                Key.OemMinus => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "_" : "-",
                Key.OemPlus => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? "+" : "=",
                _ => string.Empty,
            };
        }

        private async void SpaceSelector_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (SpaceSelector.SelectedItem is VSIXGoogleChat.Services.ChatSpace selectedSpace && _chatService != null)
            {
                if (selectedSpace.Id == _lastActiveSpaceId)
                {
                    return;
                }

                _lastActiveSpaceId = selectedSpace.Id;
                _chatService.SetCurrentSpace(selectedSpace.Id);

                if (!_isStealthMode && !_isSilentMode)
                {
                    await StopPollingMessagesAsync();
                    ClearRichTextBox(HistoryRichTextBox);

                    if (_chatOptions != null && _chatOptions.EnableNotifications)
                    {
                        RefreshHistory();
                        StartPollingMessagesAsync();
                    }
                }
            }
        }

        private (List<string> Paths, string Message) ParseFilePathsAndMessage(string input)
        {
            var paths = new List<string>();
            string message = "";

            var parts = input.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (i < parts.Length - 1)
                {
                    if (part.StartsWith("\"") && part.EndsWith("\"") && part.Length > 1)
                        part = part.Substring(1, part.Length - 2);
                    paths.Add(part);
                }
                else
                {
                    if (part.StartsWith("\""))
                    {
                        int closingQuote = part.IndexOf('"', 1);
                        if (closingQuote != -1)
                        {
                            string path = part.Substring(1, closingQuote - 1);
                            paths.Add(path);
                            message = part.Substring(closingQuote + 1).Trim();
                        }
                        else
                        {
                            paths.Add(part.Substring(1));
                        }
                    }
                    else
                    {
                        int firstSpace = part.IndexOf(' ');
                        if (firstSpace != -1)
                        {
                            string path = part.Substring(0, firstSpace);
                            paths.Add(path);
                            message = part.Substring(firstSpace + 1).Trim();
                        }
                        else
                        {
                            paths.Add(part);
                        }
                    }
                }
            }

            return (paths, message);
        }

        private async Task UploadAndSendFilesAsync(List<string> filePaths, string messageText = "", bool deleteAfterSend = false)
        {
            if (filePaths == null || !filePaths.Any())
            {
                await AppendSystemMessageAsync("No files selected to send.");
                return;
            }

            var validFilePaths = new List<string>();
            foreach (var path in filePaths)
            {
                if (File.Exists(path))
                    validFilePaths.Add(path);
                else
                    await AppendSystemMessageAsync($"File does not exist: {path}");
            }

            if (!validFilePaths.Any()) return;

            try
            {
                string filenames = string.Join(", ", validFilePaths.Select(Path.GetFileName));
                await AppendSystemMessageAsync($"Uploading {validFilePaths.Count} file(s): {filenames}...");

                if (_chatService == null)
                {
                    await AppendSystemMessageAsync("Chat service is not initialized.");
                    if (_chatOptions.EnableNotifications)
                        await Application.Current.Dispatcher.InvokeAsync(() => _errorSound?.Play());
                    return;
                }

                string? messageId = await _chatService.SendMessageWithAttachmentsAsync(messageText, validFilePaths);
                bool success = !string.IsNullOrEmpty(messageId);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (success)
                {
                    await AppendSystemMessageAsync($"{validFilePaths.Count} file(s) sent successfully: {filenames}.");
                    if (_chatOptions.EnableNotifications)
                        _successSound?.Play();

                    StartPollingMessagesAsync();

                    if (deleteAfterSend)
                    {
                        foreach (var path in validFilePaths)
                        {
                            try { File.Delete(path); } catch { }
                        }
                    }
                }
                else
                {
                    await AppendSystemMessageAsync($"Failed to send {validFilePaths.Count} file(s): {filenames}.");
                    if (_chatOptions.EnableNotifications)
                        _errorSound?.Play();
                }
            }
            catch (Exception ex)
            {
                await AppendSystemMessageAsync($"Error sending files: {ex.Message}");
                if (_chatOptions.EnableNotifications)
                    await Application.Current.Dispatcher.InvokeAsync(() => _errorSound?.Play());
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

        private void UserControl_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void UserControl_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files != null && files.Length > 0)
                {
                    _ = UploadAndSendFilesAsync(files.ToList());
                }
            }
            e.Handled = true;
        }

        private void OnPaste(object sender, DataObjectPastingEventArgs e)
        {
            var dataObject = e.DataObject;
            if (dataObject == null) return;

            try
            {
                if (dataObject.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = dataObject.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        e.CancelCommand();
                        e.Handled = true;
                        _ = UploadAndSendFilesAsync(files.ToList());
                        return;
                    }
                }

                if (dataObject.GetDataPresent(DataFormats.Bitmap))
                {
                    BitmapSource image = null;
                    try
                    {
                        image = dataObject.GetData(DataFormats.Bitmap) as BitmapSource;
                    }
                    catch { }

                    if (image == null)
                    {
                        try
                        {
                            if (Clipboard.ContainsImage())
                            {
                                image = Clipboard.GetImage();
                            }
                        }
                        catch { }
                    }

                    if (image != null)
                    {
                        e.CancelCommand();
                        e.Handled = true;
                        _ = UploadAndSendImageSourceAsync(image);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DataObject pasting handler failed: {ex.Message}");
            }
        }

        private async Task UploadAndSendImageSourceAsync(BitmapSource bitmapSource)
        {
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"pasted_image_{DateTime.Now:yyyyMMdd_HHmmss}.png");

                using (var fileStream = new FileStream(tempPath, FileMode.Create))
                {
                    BitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmapSource));
                    encoder.Save(fileStream);
                }

                await UploadAndSendFilesAsync(new List<string> { tempPath }, deleteAfterSend: true);
            }
            catch (Exception ex)
            {
                await AppendSystemMessageAsync($"Failed to process pasted image: {ex.Message}");
            }
        }

        private void ApplySuggestion(CommandSuggestion selected)
        {
            InputTextBox.Text = selected.Command + " ";
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            SuggestionsPopup.IsOpen = false;
        }

        private void SuggestionsListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var dependencyObject = (DependencyObject)e.OriginalSource;
            while (dependencyObject != null && dependencyObject != SuggestionsListBox)
            {
                if (dependencyObject is ListBoxItem item)
                {
                    if (item.DataContext is CommandSuggestion selected)
                    {
                        ApplySuggestion(selected);
                    }
                    break;
                }
                dependencyObject = VisualTreeHelper.GetParent(dependencyObject);
            }
        }
    }

    public class CommandSuggestion
    {
        public string Command { get; set; } = "";
        public string Description { get; set; } = "";
    }
}
