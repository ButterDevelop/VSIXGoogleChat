# VSIXGoogleChat

![Visual Studio](https://img.shields.io/badge/Visual%20Studio-Extension-blue?style=flat-square&logo=visual-studio)
![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)
[![Author](https://img.shields.io/badge/author-ButterDevelop-orange?style=flat-square)](https://github.com/ButterDevelop)

**VSIXGoogleChat** is a custom extension for Visual Studio that acts as a fully functional, hidden chat client disguised as a standard terminal window.

## 📖 The Story Behind It

I needed a way to chat discreetly at work without drawing unwanted attention from colleagues or managers walking by. Traditional messenger apps or web versions stand out too much on a developer's screen. So, I came up with an idea: **why not hide a chat application inside the tool I use all day?** 

Thus, VSIXGoogleChat was born. To anyone casually looking at your screen, it just looks like a regular Visual Studio tool window running `dotnet build` tasks, system logs, or console outputs.

## ✨ Key Features

- **Seamless Disguise**: The UI is designed to look exactly like a standard Visual Studio terminal output window.
- **Stealth Mode**: Instantly hide your current conversation by replacing the chat history. Supports two modes:
  - **Fake Output**: Displays highly realistic simulated `dotnet run` commands and build logs.
  - **Real Terminal**: Runs a live, hidden background PowerShell (`pwsh.exe`) instance, allowing you to run actual shell commands with full ANSI color support.
- **Silent Mode**: Mask your typing by displaying fake C# compilation messages on-screen while sending your real text secretly to Google Chat.
- **Seamless Dialogue Switching**: Instantly switch between chats via a fully customized, dark-themed space selector that preserves history scrolling state, prevents double-loading, and uses non-blocking asynchronous threads to eliminate UI freezes.
- **Upload & Send Multiple Files**: Send single or multiple files/images by typing `#file "C:\path1.png", C:\path2.png` or `#upload "C:\path1.png", C:\path2.png` (supports comma-separated lists of paths, with or without quotes, and optional message at the end).
- **Drag & Drop / Clipboard Support**: Drag and drop multiple files anywhere onto the chat window to send them instantly. Paste multiple files or raw screenshots directly from your clipboard using `Ctrl+V` or the right-click context menu.
- **Smart Chat Scrolling & Deduplication**: The chat list scrolls to the bottom on new messages only if you are already scrolled to the bottom. Integrates a message deduplication layer using a unique ID tracker to prevent double history loads when reading older messages.
- **Built-in Media Previewer**:
  - **Voice & Audio Messages**: Listen to voice notes directly in the extension. Mark them as listened, adjust volume, and control playback speed (1x, 1.2x, 1.5x, 2x) on a dedicated, clean footer panel.
  - **Positional Image Zoom**: Click images in the preview panel to toggle between **Fit to Window** and **Inspect Mode** (scales to 2.5x of the fitted size, centering the viewport on the exact coordinates where you clicked, with smooth dual-axis scrolling).
- **Secure Identity Storage**: Saves your Google Chat user identity (`my_id`) directly into the Visual Studio registry settings store via standard `DialogPage` persistence, removing local temporary files.
- **Quick Hide**: Press `Esc` to instantly close media previews or cancel multi-line inputs.

## 📥 Installation

1. Go to the [Releases](../../releases) page of this repository.
2. Download the latest `.vsix` file.
3. Close Visual Studio (if it's running).
4. Double-click the downloaded `.vsix` file to launch the VSIX Installer.
5. Follow the installation prompts.
6. Open Visual Studio and enjoy your hidden chat!

## 🛠️ Usage

- Open the chat window from the Visual Studio menu (`View` -> `Other Windows` -> `Google Chat` or via the configured shortcut).
- Use the input text box at the bottom to type and send messages. You can use special hash commands:
  - `#file "path1", path2` or `#upload` to send multiple files or images.
  - `#setname <Nickname>` to assign a custom display name to the current space (or `#setname` to clear it).
  - `#clear` or `#cls` to clear the terminal chat history on screen.
  - `#status` to check connection status, current space details, and toggle modes.
  - `#stealth` to toggle Stealth Mode.
  - `#silent` to toggle Silent Mode.
  - `#mute` to toggle sound notifications.
  - `#spaces` to print the list of all available chats and direct messages with their IDs.
  - `#help` or `#?` to display the detailed help message in chat.
- Use the toolbar buttons to toggle **Stealth Mode**, **Silent Mode**, or **Notifications**.
- Click on `[Photo]` or `[Voice]` links in the chat to open the built-in media previewer.

## 🤝 Contributing

Contributions, issues, and feature requests are welcome! Feel free to check the [issues page](../../issues). 

## 📜 License

This project is licensed under the MIT License - see the LICENSE file for details.

---
*Made with ❤️ by [ButterDevelop](https://github.com/ButterDevelop)*
