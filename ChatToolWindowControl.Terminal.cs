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
                    AddHashCommandColoredRuns(paragraph, text.Substring(0, match.Index), defaultColor);
                }

                paragraph.Inlines.Add(new Run(text.Substring(match.Index, match.Length))
                {
                    Foreground = dotnetRunColor,
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize
                });

                if (match.Index + match.Length < text.Length)
                {
                    AddHashCommandColoredRuns(paragraph, text.Substring(match.Index + match.Length), defaultColor);
                }
            }
            else
            {
                AddHashCommandColoredRuns(paragraph, text, defaultColor);
            }
        }

        private void AddHashCommandColoredRuns(Paragraph paragraph, string text, Brush defaultColor)
        {
            var regex = new Regex(@"(#\w+|#\?)", RegexOptions.Compiled);
            var matches = regex.Matches(text);
            int lastIndex = 0;

            foreach (Match m in matches)
            {
                if (m.Index > lastIndex)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(lastIndex, m.Index - lastIndex))
                    {
                        Foreground = defaultColor,
                        FontFamily = TerminalFontFamily,
                        FontSize   = TerminalFontSize
                    });
                }

                paragraph.Inlines.Add(new Run(m.Value)
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7A, 0x89, 0xC2)),
                    FontFamily = TerminalFontFamily,
                    FontSize   = TerminalFontSize,
                    FontWeight = FontWeights.Bold
                });

                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < text.Length)
            {
                paragraph.Inlines.Add(new Run(text.Substring(lastIndex))
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
            await AppendSystemMessageAsync("Stealth mode deactivated. Back to chat. Type #help to see available commands.");
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
    }
}
