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

        private void MarkVoiceMessageListened(string resourceName, bool listened, string? messageId = null)
        {
            if (string.IsNullOrEmpty(resourceName)) return;
            if (listened)
            {
                if (_listenedVoiceMessages.Add(resourceName))
                {
                    SaveListenedMessages();
                    UpdateVoiceMessageLinksInDoc(resourceName, "Listened");

                    if (_chatService != null && !string.IsNullOrEmpty(messageId))
                    {
                        _ = _chatService.AddReactionAsync(messageId, "🎧");
                    }
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
            AudioPlayerElement.MediaEnded += MediaPlayer_MediaEnded;
            AudioPlayerElement.MediaFailed += MediaPlayer_MediaFailed;

            UpdateSpeedButtonsHighlight();

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
                AudioStatusTextBlock.Text = $"Voice Message ({duration:mm\\:ss})";
                TimeTotalTextBlock.Text = $"{duration:mm\\:ss}";
                AudioProgressSlider.Maximum = duration.TotalSeconds;
            }
            else
            {
                AudioStatusTextBlock.Text = "Voice Message (Duration: Unknown)";
                TimeTotalTextBlock.Text = "--:--";
            }

            double savedSeconds = 0;
            if (_activeAudioAttachment != null)
            {
                savedSeconds = GetSavedVoiceMessageTiming(_activeAudioAttachment.Name);
            }

            if (savedSeconds > 0 && AudioPlayerElement.NaturalDuration.HasTimeSpan && savedSeconds < AudioPlayerElement.NaturalDuration.TimeSpan.TotalSeconds - 1)
            {
                AudioPlayerElement.Position = TimeSpan.FromSeconds(savedSeconds);
                AudioProgressSlider.Value = savedSeconds;
                TimeElapsedTextBlock.Text = $"{AudioPlayerElement.Position:mm\\:ss}";
            }
            else
            {
                AudioProgressSlider.Value = 0;
                TimeElapsedTextBlock.Text = "00:00";
            }

            AudioPlayerElement.SpeedRatio = _currentSpeed;

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
                if (_activeAudioAttachment != null)
                {
                    SetVoiceMessageTiming(_activeAudioAttachment.Name, 0);
                    SaveVoiceMessageTimings();
                }
                StopAudio();
                if (_activeAudioAttachment != null)
                {
                    MarkVoiceMessageListened(_activeAudioAttachment.Name, true, _activeAudioAttachment.MessageId);
                    ListenedCheckBox.IsChecked = true;

                    bool playedNext = PlayNextVoiceMessage();
                    if (!playedNext)
                    {
                        CloseMediaPanel(false);
                    }
                }
            }));
        }

        private bool PlayNextVoiceMessage()
        {
            if (_activeAudioAttachment == null) return false;

            int currentIndex = _sessionAudioAttachments.FindIndex(att => att.Name == _activeAudioAttachment.Name);
            if (currentIndex >= 0 && currentIndex + 1 < _sessionAudioAttachments.Count)
            {
                var nextAudioAttachment = _sessionAudioAttachments[currentIndex + 1];
                _ = OpenMediaPreviewAsync(nextAudioAttachment);
                return true;
            }
            return false;
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

                if (_activeAudioAttachment != null)
                {
                    SetVoiceMessageTiming(_activeAudioAttachment.Name, AudioPlayerElement.Position.TotalSeconds);
                }
            }
        }

        private void PlayAudio()
        {
            AudioPlayerElement.Play();
            AudioPlayerElement.SpeedRatio = _currentSpeed;
            _mediaTimer?.Start();
            AudioStatusTextBlock.Text = "Playing Voice Message...";
        }

        private void PauseAudio()
        {
            AudioPlayerElement.Pause();
            _mediaTimer?.Stop();
            AudioStatusTextBlock.Text = "Paused";

            if (_activeAudioAttachment != null && AudioPlayerElement.Position != null)
            {
                SetVoiceMessageTiming(_activeAudioAttachment.Name, AudioPlayerElement.Position.TotalSeconds);
                SaveVoiceMessageTimings();
            }
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
            if (_activeAudioAttachment != null)
            {
                SetVoiceMessageTiming(_activeAudioAttachment.Name, 0);
                SaveVoiceMessageTimings();
            }
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
            if (_activeAudioAttachment != null)
            {
                SetVoiceMessageTiming(_activeAudioAttachment.Name, AudioProgressSlider.Value);
                SaveVoiceMessageTimings();
            }
        }

        private void ListenedCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (_activeAudioAttachment != null)
            {
                MarkVoiceMessageListened(_activeAudioAttachment.Name, true, _activeAudioAttachment.MessageId);
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
            if (_activeAudioAttachment != null && AudioPlayerElement.Position != null)
            {
                double pos = AudioPlayerElement.Position.TotalSeconds;
                if (pos > 0)
                {
                    SetVoiceMessageTiming(_activeAudioAttachment.Name, pos);
                    SaveVoiceMessageTimings();
                }
            }

            if (pauseOnly)
                PauseAudio();
            else
            {
                StopAudio();
                AudioPlayerElement.Source = null; // Clear the media source to release the file lock so the temporary audio file can be deleted
            }
            MediaPanel.Visibility = Visibility.Collapsed;
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
            FileNameTextBlock.Text = att.ContentName;
            FileMimeTextBlock.Text = errorMessage ?? $"File Type: {att.ContentType}";
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
            if (PreviewImage.Source == null) return;

            // Reset ScaleTransform to 1.0 since we are sizing the image directly
            ImageScale.ScaleX = 1.0;
            ImageScale.ScaleY = 1.0;

            if (double.IsNaN(PreviewImage.Width) && double.IsNaN(PreviewImage.Height))
            {
                // Zoom In: Determine aspect ratios
                double imgWidth = PreviewImage.Source.Width;
                double imgHeight = PreviewImage.Source.Height;
                if (imgWidth <= 0 || imgHeight <= 0) return;

                double viewportWidth = ImageScrollViewer.ViewportWidth;
                double viewportHeight = ImageScrollViewer.ViewportHeight;
                if (viewportWidth <= 0 || viewportHeight <= 0) return;

                // Calculate click position relative to current actual image size (fitted)
                Point clickPos = e.GetPosition(PreviewImage);
                double pctX = clickPos.X / PreviewImage.ActualWidth;
                double pctY = clickPos.Y / PreviewImage.ActualHeight;

                // Calculate fitted size of the image inside the viewport
                double scale = Math.Min(viewportWidth / imgWidth, viewportHeight / imgHeight);
                double fitWidth = imgWidth * scale;
                double fitHeight = imgHeight * scale;

                // Zoom In: Scale to 2.5x of the fitted size, but ensure it is at least the viewport size to eliminate empty side bars
                double zoomFactor = 2.5;
                PreviewImage.Width = Math.Max(fitWidth * zoomFactor, viewportWidth);
                PreviewImage.Height = Math.Max(fitHeight * zoomFactor, viewportHeight);
                PreviewImage.Stretch = Stretch.UniformToFill;

                // Enable scrollbars in both directions
                ImageScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
                ImageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

                // Force layout update to recalculate scroll bounds
                ImageScrollViewer.UpdateLayout();

                // Scroll to center the clicked point
                double targetX = PreviewImage.Width * pctX - viewportWidth / 2;
                double targetY = PreviewImage.Height * pctY - viewportHeight / 2;

                ImageScrollViewer.ScrollToHorizontalOffset(targetX);
                ImageScrollViewer.ScrollToVerticalOffset(targetY);
            }
            else
            {
                // Zoom Out: Revert to auto sizing (Stretch.Uniform handles the fitting)
                PreviewImage.Width = double.NaN;
                PreviewImage.Height = double.NaN;
                PreviewImage.Stretch = Stretch.Uniform;
                ImageScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                ImageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
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

            MediaPanel.Visibility = Visibility.Visible;

            // Ensure the panel is expanded when opening new media
            _isMediaPanelCollapsed = false;
            CollapseMediaButton.Visibility = Visibility.Visible;
            ExpandMediaButton.Visibility = Visibility.Collapsed;
            MediaPanel.VerticalAlignment = VerticalAlignment.Stretch;

            ImagePreviewContainer.Visibility = Visibility.Collapsed;
            AudioPreviewGrid.Visibility = Visibility.Collapsed;
            AudioControlsBorder.Visibility = Visibility.Collapsed;
            FilePreviewGrid.Visibility = Visibility.Collapsed;

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
                MediaTitleTextBlock.Text = "DOWNLOAD ERROR";
                FileNameTextBlock.Text = att.ContentName;
                FileMimeTextBlock.Text = $"Error: {ex.Message}";
                FilePreviewGrid.Visibility = Visibility.Visible;
                return;
            }

            _activeMediaLocalPath = tempPath;

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

                    // Revert to fit-to-window view by default when loading a new image
                    PreviewImage.Width = double.NaN;
                    PreviewImage.Height = double.NaN;
                    PreviewImage.Stretch = Stretch.Uniform;
                    ImageScale.ScaleX = 1.0;
                    ImageScale.ScaleY = 1.0;
                    ImageScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                    ImageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

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

                AudioPreviewGrid.Visibility = Visibility.Visible;
                AudioControlsBorder.Visibility = Visibility.Visible;

                AudioStatusTextBlock.Text = "Voice Message Loaded";
                ListenedCheckBox.IsChecked = IsVoiceMessageListened(att.Name);

                try
                {
                    var targetUri = new Uri(tempPath, UriKind.Absolute);
                    if (AudioPlayerElement.Source == targetUri)
                    {
                        double savedSeconds = GetSavedVoiceMessageTiming(att.Name);
                        if (savedSeconds > 0 && AudioPlayerElement.NaturalDuration.HasTimeSpan && savedSeconds < AudioPlayerElement.NaturalDuration.TimeSpan.TotalSeconds - 1)
                        {
                            AudioPlayerElement.Position = TimeSpan.FromSeconds(savedSeconds);
                            AudioProgressSlider.Value = savedSeconds;
                            TimeElapsedTextBlock.Text = $"{AudioPlayerElement.Position:mm\\:ss}";
                        }
                        PlayAudio();
                    }
                    else
                    {
                        _autoplayOnOpen = true;
                        AudioPlayerElement.Source = targetUri;
                    }
                    MarkVoiceMessageListened(att.Name, true, att.MessageId);
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
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/gif" => ".gif",
                "audio/mpeg" or "audio/mp3" => ".mp3",
                "audio/m4a" => ".m4a",
                "audio/wav" or "audio/x-wav" => ".wav",
                "audio/ogg" or "audio/x-ogg" => ".ogg",
                "video/mp4" => ".mp4",
                "text/plain" => ".txt",
                "application/pdf" => ".pdf",
                _ => ".dat"
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
                    using var g = System.Drawing.Graphics.FromImage(bmp);
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
                    byte[] wavData = CreateBeepWavBytes(800, 1500); // Generate a 1.5-second audio beep at 800 Hz to simulate compiling sound
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

            int numSamples = sampleRate * durationMs / 1000;
            int dataChunkSize = numSamples * channels * bitsPerSample / 8;
            int fileSize = 36 + dataChunkSize;

            using var ms = new MemoryStream();
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
                short sample = (short)(Math.Sin(t) * short.MaxValue * 0.5); // Compute the sine wave sample at 50% amplitude (volume)
                writer.Write(sample);
                t += dt;
            }

            writer.Flush();
            return ms.ToArray();
        }

        private void UpdateSpeedButtonsHighlight()
        {
            if (Speed10Button == null || Speed12Button == null || Speed15Button == null || Speed20Button == null)
                return;

            Speed10Button.Foreground = _currentSpeed == 1.0 ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66)) : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            Speed12Button.Foreground = _currentSpeed == 1.2 ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66)) : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            Speed15Button.Foreground = _currentSpeed == 1.5 ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66)) : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            Speed20Button.Foreground = _currentSpeed == 2.0 ? new SolidColorBrush(Color.FromRgb(0x00, 0xCC, 0x66)) : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));

            Speed10Button.FontWeight = _currentSpeed == 1.0 ? FontWeights.Bold : FontWeights.Normal;
            Speed12Button.FontWeight = _currentSpeed == 1.2 ? FontWeights.Bold : FontWeights.Normal;
            Speed15Button.FontWeight = _currentSpeed == 1.5 ? FontWeights.Bold : FontWeights.Normal;
            Speed20Button.FontWeight = _currentSpeed == 2.0 ? FontWeights.Bold : FontWeights.Normal;
        }

        private void SpeedPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                if (btn == Speed10Button)       _currentSpeed = 1.0;
                else if (btn == Speed12Button)  _currentSpeed = 1.2;
                else if (btn == Speed15Button)  _currentSpeed = 1.5;
                else if (btn == Speed20Button)  _currentSpeed = 2.0;

                AudioPlayerElement.SpeedRatio = _currentSpeed;
                UpdateSpeedButtonsHighlight();
            }
        }

        private string GetVoiceMessageTimingsFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localAppData, "VSIXInternalPowerShell");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "voice_message_timings.txt");
        }

        private void LoadVoiceMessageTimings()
        {
            try
            {
                string path = GetVoiceMessageTimingsFilePath();
                if (File.Exists(path))
                {
                    var lines = File.ReadAllLines(path);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 2 && double.TryParse(parts[1], out double seconds))
                        {
                            _voiceMessageTimings[parts[0]] = seconds;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"LoadVoiceMessageTimings error: {ex.Message}");
            }
        }

        private void SaveVoiceMessageTimings()
        {
            try
            {
                string path = GetVoiceMessageTimingsFilePath();
                var lines = _voiceMessageTimings.Select(kvp => $"{kvp.Key}|{kvp.Value}");
                File.WriteAllLines(path, lines);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveVoiceMessageTimings error: {ex.Message}");
            }
        }

        private double GetSavedVoiceMessageTiming(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            return _voiceMessageTimings.TryGetValue(name, out double secs) ? secs : 0;
        }

        private void SetVoiceMessageTiming(string name, double seconds)
        {
            if (string.IsNullOrEmpty(name)) return;
            _voiceMessageTimings[name] = seconds;
        }

        private void CollapseMediaButton_Click(object sender, RoutedEventArgs e)
        {
            CollapseMediaPanel();
        }

        private void ExpandMediaButton_Click(object sender, RoutedEventArgs e)
        {
            ExpandMediaPanel();
        }

        private void CollapseMediaPanel()
        {
            if (MediaPanel.Visibility != Visibility.Visible || _isMediaPanelCollapsed)
                return;

            _isMediaPanelCollapsed = true;
            CollapseMediaButton.Visibility = Visibility.Collapsed;
            ExpandMediaButton.Visibility = Visibility.Visible;

            ImagePreviewContainer.Visibility = Visibility.Collapsed;
            AudioPreviewGrid.Visibility = Visibility.Collapsed;
            AudioControlsBorder.Visibility = Visibility.Collapsed;
            FilePreviewGrid.Visibility = Visibility.Collapsed;

            MediaTitleTextBlock.Visibility = Visibility.Collapsed;
            MediaHeaderGrid.Margin = new Thickness(0);
            MediaPanel.Width = 75;

            MediaPanel.VerticalAlignment = VerticalAlignment.Top;
        }

        private void ExpandMediaPanel()
        {
            if (MediaPanel.Visibility != Visibility.Visible || !_isMediaPanelCollapsed)
                return;

            _isMediaPanelCollapsed = false;
            CollapseMediaButton.Visibility = Visibility.Visible;
            ExpandMediaButton.Visibility = Visibility.Collapsed;

            MediaTitleTextBlock.Visibility = Visibility.Visible;
            MediaHeaderGrid.Margin = new Thickness(0, 0, 0, 10);
            MediaPanel.Width = 240;

            MediaPanel.VerticalAlignment = VerticalAlignment.Stretch;

            RestoreActiveMediaVisibility();
        }

        private void RestoreActiveMediaVisibility()
        {
            if (_activeAudioAttachment != null)
            {
                AudioPreviewGrid.Visibility = Visibility.Visible;
                AudioControlsBorder.Visibility = Visibility.Visible;
            }
            else if (_activeMediaLocalPath != null)
            {
                if (_previewAttachments.Count > 0 && _previewAttachments[_currentPreviewIndex].ContentType.StartsWith("image/"))
                {
                    ImagePreviewContainer.Visibility = Visibility.Visible;
                }
                else
                {
                    FilePreviewGrid.Visibility = Visibility.Visible;
                }
            }
        }

        #endregion
    }
}
