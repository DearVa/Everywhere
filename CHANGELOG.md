## [v0.6.0](https://github.com/DearVa/Everywhere/releases/tag/v0.6.0) - 2026-01-21

### ✨ Features
- 🎉 **macOS Support**: Native macOS support is here! We have brought a complete experience almost identical to the Windows version.
- Added "Selection Context" (Experimental): When enabled in settings, the selected text and its context will be automatically attached, providing better context understanding for translation and explanation tasks.
- Added a button to open the Settings window directly from the Chat Window.

### 🚀 Improvements
- Improved encoding detection accuracy for the file reader tool.
- Optimized prompts for the web search tool.

### 🐛 Bug Fixes
- Fixed broken support for DeepSeek reasoning models.
- Fixed an issue where token usage statistics for Claude models were displayed incorrectly.
- Fixed an issue where files could not be pasted correctly if the file path contained spaces.
- Fixed an issue where the color picker in the assistant icon editor was unresponsive.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.11...v0.6.0



## [v0.5.11](https://github.com/DearVa/Everywhere/releases/tag/v0.5.11) - 2026-01-12

### 🐛 Bug Fixes
- Model provider combobox is empty in preset mode (#254)

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.10...v0.5.11



## [v0.5.10](https://github.com/DearVa/Everywhere/releases/tag/v0.5.10) - 2026-01-12

### 🐛 Bug Fixes
- (Critical) Fixed an issue where Everywhere cannot get details/contents of UI elements.
- (Windows) Fixed an issue where API keys cannot be configured when startup as administrator.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.9...v0.5.10



## [v0.5.9](https://github.com/DearVa/Everywhere/releases/tag/v0.5.9) - 2026-01-10

### 🐛 Bug Fixes
- Fixed an issue where the display order of content in the ApiKey selection box was incorrect.
- (Windows) Fixed an issue where custom assistants were not migrating correctly.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.8...v0.5.9



## [v0.5.8](https://github.com/DearVa/Everywhere/releases/tag/v0.5.8) - 2026-01-08

### ✨ Features
- Added a screenshot tool for quickly attaching screen captures in the chat window.
- Added support for the UniFuncs search engine.

### 🔄️ Changed
- Removed the unstable UI element manipulation tool.

### 🐛 Bug Fixes
- Fixed a critical issue where assistant preset modes were not correctly applied.
- Fixed an issue where assistants could not be duplicated.
- Fixed styling issues on the Welcome Page.
- Fixed an issue where the update check timestamp did not use the local time zone.
- (Windows) Fixed an issue where the PowerShell plugin failed to refresh environment variables.
- (Windows) Fixed an issue where the main window could incorrectly remain always-on-top.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.7...v0.5.8



## [v0.5.7](https://github.com/DearVa/Everywhere/releases/tag/v0.5.7) - 2026-01-06

⚠️ IMPORTANT WARNING
If upgrading from v0.5.5 or earlier, this version will automatically migrate your settings file. It is recommended to backup `C:\Users\[Username]\AppData\Roaming\Everywhere\settings.json` beforehand. Migration completes upon startup. All existing API keys will be cleared and moved to the `LegacyApiKeys` property in `settings.json`. You will need to reconfigure them. For security reasons, please delete `LegacyApiKeys` immediately after reconfiguring your keys.

### ✨ Features
- Improved tray icon interaction: Single-click to open the Chat Window, double-click to open the Main Window.

### 🐛 Bug Fixes
- (Important) Fixed an issue where "Advanced Configuration" settings were not applied correctly.
- Fixed an issue where the Main Window could not be restored from the tray menu if it was minimized.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.6...v0.5.7



## [v0.5.6](https://github.com/DearVa/Everywhere/releases/tag/v0.5.6) - 2026-01-05

### ⚠️ Important Changes
**Security Update**: API keys are now stored using a more secure encryption method.

**IMPORTANT WARNING**: This version will automatically migrate your settings file. It is recommended to backup `C:\Users\[Username]\AppData\Roaming\Everywhere\settings.json` beforehand. Migration completes upon startup. All existing API keys will be cleared and moved to the `LegacyApiKeys` property in `settings.json`. You will need to reconfigure them. For security reasons, please delete `LegacyApiKeys` immediately after reconfiguring your keys.

### ✨ Features
- **New "Essential" Plugin**: Adds support for running Sub-Agents and managing Todo lists, enhancing LLM performance on complex tasks.
- **Refactored Custom Assistant & Welcome Page**: Now features "Preset Mode" and "Advanced Mode" for a more user-friendly experience.
- Added the ability to import MCP servers from JSON (#191).
- Added a setting to automatically create a new chat every time the chat window is opened (#32).

### 🚀 Improvements
- Assistants now reliably use the correct working directory instead of defaulting to `C:\Windows\System32` (#161).
- Optimized the default system prompt.
- Improved the UI for checking updates.
- Various other UI/UX improvements.

### 🐛 Bug Fixes
- Fixed a critical issue where file attachments could cause an infinite loop and memory exhaustion crash.
- Fixed an issue where Gemini 3 might fail when calling tools.
- Fixed an issue where the File System plugin returned incomplete paths during file search.
- Fixed an issue where the PowerShell plugin failed to capture Warning output (#225).
- Fixed encoding issues in the PowerShell plugin.
- Fixed an issue where LaTeX formulas were not clearly visible in Light Mode.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.5...v0.5.6



## [v0.5.5](https://github.com/DearVa/Everywhere/releases/tag/v0.5.5) - 2025-12-21

### 🚀 Improvements
- Optimized various UI visual details.

### 🐛 Bug Fixes
- Fixed an issue where the "Everywhere minimized to tray" notification appeared every time.
- Fixed an issue where the enabled state of MCP tools failed to load correctly.
- Fixed an issue where the tool permission consent dialog failed to display.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.4...v0.5.5



## [v0.5.4](https://github.com/DearVa/Everywhere/releases/tag/v0.5.4) - 2025-12-20

### ⚠️ Important Changes
- Changed the software license from Apache 2.0 to **BSL 1.1**.

### ✨ Features
- 🎨 Brand New UI: 
  - A complete refactor of the user interface, featuring a modern design, responsive layout, and a fresh chat window style with Light Mode support. The app now automatically syncs with the system accent color and includes more helpful tooltips.
- Added support for the OpenAI Responses API.
- Added shortcuts for quick navigation:
  - Scroll on the assistant icon or use `Ctrl` + `0-9` keys to switch assistants.
  - Press `Ctrl` + `H` to toggle the chat history view.

### 🚀 Improvements
- Window position and size (for both Settings and Chat) are now automatically saved and restored.
- Improved the visual style of the element selector popup.
- Streamlined the MCP configuration process.
- Font size settings now correctly apply to the chat window.

### 🐛 Bug Fixes
- Fixed an issue where the element selector was offset on multi-monitor setups with different scaling factors (#17).
- Fixed an issue where the screenshot tool could freeze (#177).
- Fixed an issue where Anthropic API calls would fail (#198).
- Fixed an issue where DeepSeek models encountered errors when using tools during the reasoning process (#208).
- Fixed a potential memory leak (#207).

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.3...v0.5.4



## [v0.5.3](https://github.com/DearVa/Everywhere/releases/tag/v0.5.3) - 2025-12-7

### ✨ Features
- Added a debug option in settings to create a process dump.

### 🚀 Improvements
- Improved the UI display for reasoning output.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.2...v0.5.3



## [v0.5.2](https://github.com/DearVa/Everywhere/releases/tag/v0.5.2) - 2025-12-3

Thanks to [@AidenNovak](https://github.com/AidenNovak) for sponsoring the Apple Developer Program fee. The Mac version is in its final stages of development and will be released soon.

### ✨ Features
- Added support for the Gemini 3 Pro protocol.
- Added the ability to export as Markdown (#180).
- Custom assistants now support drag-and-drop sorting.

### 🐛 Bug Fixes
- Fixed an issue where Google search was not working.
- Fixed an issue where data was lost when copying custom assistants.
- Fixed an issue with abnormal background CPU usage.
- Fixed an issue where Gemini could not call tools (#174).
- Fixed an issue where the settings button in the chat window incorrectly received focus (#173).
- Fixed an issue where rendered Markdown checkboxes were too large (#172).
- Fixed an issue where parameter types were incorrectly converted to strings during MCP calls (#167).
- Fixed a missing reset button in the model settings template (#149, thanks to @ChuheLin).

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.1...v0.5.2



## [v0.5.1](https://github.com/DearVa/Everywhere/releases/tag/v0.5.1) - 2025-11-24

### ✨ Features
- Added a digital signature to the software (Thanks to Certum).
- Desktop notifications will now be displayed for permission consent when the chat window is hidden.

### 🚀 Improvements
- Enabled trimming optimization, reducing the application size by approximately 50%.
- Optimized the terminal plugin.

### 🐛 Bug Fixes
- Fixed an issue where some MCP plugins continued to run in the background after the application was closed.
- Fixed an issue where settings were sometimes not saved correctly.
- Fixed an issue where terminal execution output was sometimes not displayed.
- Fixed an issue where some plugin icons were not displayed.
- Fixed an issue where the chat window could not be closed using the shortcut key.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.5.0...v0.5.1



## [v0.5.0](https://github.com/DearVa/Everywhere/releases/tag/v0.5.0) - 2025-11-22

### ✨ Features
- 🎉 **Major Update: Added support for MCP (Model Context Protocol)!** You can now add your own MCP tools, which will be automatically enabled when called by an assistant. Supports Stdio, Streamable HTTP, and SSE protocols.

### 🚀 Improvements
- When a tool called by an assistant is not found, it now performs a fuzzy match and informs the assistant, reducing model hallucinations.
- (Windows) Added output display for `Everything` plugin.
- (Windows) Removed `Windows System API` plugin.
- Other UI adjustments and bug fixes.

### 🐛 Bug Fixes
- Fixed an issue where custom assistant avatars were stretched (#155).
- Fixed an issue where Markdown content was difficult to select and could cause errors when copying (#91).
- Fixed an issue where bold or italic text in Markdown would revert to normal font when selected (#114).
- Fixed a bug where capturing a UI element could not be canceled.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.7...v0.5.0



## [v0.4.7](https://github.com/DearVa/Everywhere/releases/tag/v0.4.7) - 2025-11-19

### ✨ Features
- Added the ability to copy and edit sent messages. You can also hold `Shift` while clicking the copy button to get the raw message content (#70).
- Added a setting to adjust the font size (#47).
- Added a button to duplicate custom assistants (#150).
- Added support for Gemini 3 Pro Preview.

### 🚀 Improvements
- Optimized window positioning and resizing behavior. The chat window now opens centered and remembers its last position. The window size is no longer reset when switching chats unless manually minimized.

### 🐛 Bug Fixes
- Fixed an issue where adding an attachment could result in an incorrect file extension.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.6...v0.4.7



## [v0.4.6](https://github.com/DearVa/Everywhere/releases/tag/v0.4.6) - 2025-11-15

### ✨ Features
- Added native support for the Gemini schema, which fixes the issue where Gemini could not upload images (#125).
- Added drag-and-drop support for files and text (#135).

### 🚀 Improvements
- Improved error message display (e.g., context length exceeded, network proxy errors, and duplicated error messages).
- Improved UI appearance and visual effects.

### 🐛 Bug Fixes
- Fixed an issue where the log folder could not be opened if the path contained non-ASCII characters (such as Chinese) (#117).
- Fixed some UI elements not responding correctly when switching languages.
- Fixed garbled UI text for English users in some cases.
- (Windows) Fixed an issue where terminal execution could sometimes not be terminated, causing the application to freeze.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.5...v0.4.6



## [v0.4.5](https://github.com/DearVa/Everywhere/releases/tag/v0.4.5) - 2025-11-13

### ✨ Features
- Added a setting for model request timeout.
- Added shortcuts in settings to "Edit configuration file" and "Open log folder" (Thanks @TheNotoBarth).
- Added rendering support for LaTeX formulas.

### 🚀 Improvements
- Network proxy settings now take effect immediately without requiring an application restart.
- Improved error messages for large language models (Thanks @TheNotoBarth).
- Optimized the loading performance of the settings page.
- Removed the unsupported `x-ai/grok-4-fast:free` model from OpenRouter.
- Improved the display of some error messages.

### 🐛 Bug Fixes
- Refactored the underlying data model to fix a bug where messages could be displayed repeatedly.
- Fixed a potential memory leak.
- Optimized the display logic for dialog boxes to prevent them from going beyond the window boundaries and becoming inoperable.
- Fixed an issue where model requests could not handle redirects automatically.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.4...v0.4.5



## [v0.4.4](https://github.com/DearVa/Everywhere/releases/tag/v0.4.4) - 2025-11-5

- 📢 The macOS version is on the way and is expected to be released in v0.5.0. Good things take time, so please be patient!

### ✨ Features
- Added network proxy settings, which can be configured manually. **A restart is required for changes to take effect!**
- Added language support for Spanish, Russian, French, Italian, Japanese, Korean, Turkish, and Traditional Chinese (translated by GPT-4o).

### 🚀 Improvements
- Optimized chat plugins:
  - Chat plugin features now display user-friendly descriptions.
  - PowerShell now shows detailed explanations, specific commands, and the final output.
  - For security reasons, the "Allow for this session" and "Always allow" options are not available for multi-line PowerShell script execution.

### 🐛 Bug Fixes
- Fixed an issue where PowerShell execution could not be interrupted (#104).

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.3...v0.4.4



## [v0.4.3](https://github.com/DearVa/Everywhere/releases/tag/v0.4.3) - 2025-11-2

### ✨ Features
- Added Türkçe language support (Thanks @complex-cgn)
- Improved chat history viewing & management (including topic editing and multi-selecting/deleting chats)

### 🚀 Improvements
- Reduced memory usage & UI freeze when rendering markdown codeblocks (2700% improvement)
- Added more icons to the assistant IconEditor and optimized its performance

### 🐛 Bug Fixes
- (Windows) Fixed an issue where Everywhere could prevent system shutdown
- Fixed an issue where the chat window could not be closed by pressing the Esc key
- Fixed an issue where canceling a tool call could prevent the conversation from continuing
- Fixed an issue where some reasoning-focused LLMs could not use tools correctly

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.2...v0.4.3



## [v0.4.2](https://github.com/DearVa/Everywhere/releases/tag/v0.4.2) - 2025-10-28

### ✨ Features
- Now you can upload documents (PDF, Word, Text, etc.) directly in the chat window as attachments for context (⚠️ only supported by models that allow file inputs)

### 🚀 Improvements
- Chat window can be closed when press shortcut again
- Optimized encoding handling in "File system" chat plugin

### 🐛 Bug Fixes
- Fixed chat topic may not generate correctly for some models
- Fixed "Web search" chat tool may report `count out of range` error
- Fixed "Scroll to end" button in chat window mistakenly get focused
- (Windows) Fixed missing `Everything.dll`
- (Windows) Fixed chat window still appears in `Alt+Tab` list when closed
- (Windows) Fixed chat window disappears when picking a file
- (Windows) Fixed icon & title of update notify is missing

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.1...v0.4.2



## [v0.4.1](https://github.com/DearVa/Everywhere/releases/tag/v0.4.1) - 2025-10-27

### ⚠️ BREAKING CHANGE: Chat window shortcut will reset to `Ctrl+Shift+E` due to renaming "Hotkey" to "Shortcut".
️
### 🚀 Improvements
- Renamed DeepSeek models to their new official names
- I18N: Changed "Hotkey" to "Shortcut"
- Refactored "operate UI elements" chat tool for better stability

### 🐛 Bug Fixes
- Fixed window cannot maximize when clicking the maximize button
- Fixed "Web snapshot" chat tool not working
- Fixed "everything" chat tool cannot work when "file system" chat tool is enabled
- Fixed token counting may be bigger than actual usage in some cases

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.4.0...v0.4.1



## [v0.4.0](https://github.com/DearVa/Everywhere/releases/tag/v0.4.0) - 2025-10-26

### ✨ Features
- Plugin Execution Feedback
  - High-permission plugins now require user confirmation before running
  - Results of plugin calls are now displayed in the chat window 
    - File system plugin lists which files were accessed
    - File changes are shown and can be reviewed before applying
    - Web search now displays the specific query being used
- Temporary Chat
  - Temporary chats that are not saved will now be automatically deleted when switching to another chat or creating a new one
  - You can choose to automatically enter temporary chat mode in settings
- Web Search Enhancements
  - Added Jina as a web search provider
  - Added SearXNG as a web search provider
- Added settings for controlling visual context usage and detail level
- Chat window now displays the current chat title
- (Windows) Integrated Everything to accelerate local file searches

### 🚀 Improvements
- Improved the main window UI layout & style
- Enabled right-click context menu (copy, cut, paste) in the chat input box
- Added a scroll-to-bottom button in the chat window
- Added more emoji choices for custom assistants

### 🐛 Bug Fixes
- Fixed chat history was sometimes not sorted correctly
- Fixed the `Alt` key could not be used as a hotkey
- Fixed the default assistant's icon could not be changed
- Fixed the element picker could not be closed with a right-click
- Fixed the main window would appear after picking an element
- Fixed potential unresponsiveness issues during chats
- Fixed connection test failures for Custom Assistants
- Fixed chat title generation may fail for some models
- Fixed fonts may become _Italic_ unexpectedly
- Fixed a dead link in the Welcome Dialog
- Fixed a recursive self-reference issue
- Fixed wrong acrylic effect on Windows 10

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.12...v0.4.0



## [v0.3.12](https://github.com/DearVa/Everywhere/releases/tag/v0.3.12) - 2025-10-16

### 🚀 Improvements
- Removed the obsolete Bing web search engine
- Optimized error handling

### 🐛 Bug Fixes
- Fixed an issue where the chat window could not be resized
- Fixed an issue where the Tavily search engine could not be invoked
- Fixed an issue where the chat action bubble did not display error messages
- Fixed an issue where variables in the system prompt were not rendered
- Fixed an issue where the chat topic summary was sometimes empty (Note: This is not fully resolved, as some models may still produce empty results)

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.11...v0.3.12



## [v0.3.11](https://github.com/DearVa/Everywhere/releases/tag/v0.3.11) - 2025-10-16

### ⚠️ Breaking Changes ⚠️
Due to the model configuration page being rebuilt, previously configured model settings (including API keys, etc.) will be lost! However, they still exist in the software settings file. Advanced users can find them at `C:\Users\<username>\AppData\Roaming\Everywhere\settings.json`.

### ✨ Features
- 🎉 Added custom assistants! You can now create multiple assistants with different icons, names, and prompts, and switch between them freely during a chat
- Added support for the Tavily web search engine

### 🚀 Improvements
- Optimized exception handling

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.10...v0.3.11



## [v0.3.10](https://github.com/DearVa/Everywhere/releases/tag/v0.3.10) - 2025-10-14

### 🚀 Improvements
- Introduced a new, modern installer that remembers the previous installation location during updates

### 🐛 Bug Fixes
- Fixed an issue where an error was thrown if the OpenAI API key was empty (which is allowed for services like LM Studio)
- Fixed a bug that prevented pasting images as attachments in some cases
- Fixed a bug that caused the application to freeze when sending messages with images
- Fixed an issue causing an HTTP 400 error during function calls
- Fixed an issue where requests could be blocked by Cloudflare from some third-party model providers

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.9...v0.3.10



## [v0.3.9](https://github.com/DearVa/Everywhere/releases/tag/v0.3.9) - 2025-10-13

### ✨ Features
- Provider icons in settings are now loaded as local resources for faster display
- Added deep-thought output support for Ollama, SiliconFlow, and some OpenAI-compatible models; fixed SiliconFlow and similar models not outputting results
- Added option to show chat plugin permissions in settings

### 🚀 Improvements
- Enhanced error handling and user-friendly messages

### 🐛 Bug Fixes
- Fixed dialog covering the title bar, making the window undraggable or unresponsive
- Fixed some prompt tasks (e.g. translation) may use the wrong target language

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.8...v0.3.9



## [v0.3.8](https://github.com/DearVa/Everywhere/releases/tag/v0.3.8) - 2025-10-11

### ✨ Features
- Software updates can now be cancelled by dismissing the toast notification
- Added more keyboard shortcuts: `Ctrl+N` for a new chat, `Ctrl+T` to for tools switch
- Added a visual tree length limit setting to save tokens
- Added a notification when an update is available

### 🚀 Improvements
- Optimized the button layout in the chat window
- Added more friendly error messages for a better user experience

### 🐛 Bug Fixes
- Fixed a potential error when loading settings
- Fixed an issue where the chat window could not be reopened after being accidentally closed
- Fixed a missing scrollbar on the chat plugin page (#28)
- Fixed unnecessary telemetry logging
- Corrected a typo for an Ollama model: deepseek R1 8B -> deepseek R1 7B

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.7...v0.3.8



## [v0.3.7](https://github.com/DearVa/Everywhere/releases/tag/v0.3.7) - 2025-10-11

### 🐞 Fixed
- Fixed error messages were incorrectly parsed as "unknown".

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.6...v0.3.7



## [v0.3.6](https://github.com/DearVa/Everywhere/releases/tag/v0.3.6) - 2025-10-10

### ✨ New Features
- Added chat statistics in the chat window, which can be toggled in settings.
- Added a setting to control whether to automatically attach the focused element when opening the chat window.
- Added a setting to allow the model to continue generating responses in the background after the chat window is closed.
- Added support for `Claude Sonnet 4.5`.

### 🔄️ Changed
- Improved tooltips for plugin settings.
- Most error messages are now translated and provide more detailed hints.
- Improved the download speed and stability of in-app updates.
- Model parameter settings are now expanded by default to prevent them from being missed.

### 🐞 Fixed
- Fixed an issue where the model's tool-call usage was displayed in the wrong position.
- Fixed an issue where the chat window could not be reopened after being closed while a message was being streamed.
- Fixed an issue where the `Shift` and `Win` keys could become unresponsive if a hotkey included the `Win` key. You can now set the Copilot key as a hotkey normally.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.5...v0.3.6



## [v0.3.5](https://github.com/DearVa/Everywhere/releases/tag/v0.3.5) - 2025-10-09

### 🐞 Fixed
- Fixed hotkey input box crashes when clicking twice [#20](https://github.com/DearVa/Everywhere/issues/20)
- Fixed potential null pointer error when sending message
- Fixed wrong telemetry log level

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.4...v0.3.5



## [v0.3.4](https://github.com/DearVa/Everywhere/releases/tag/v0.3.4) - 2025-10-09

### 🔄️ Changed
- Improved user prompt for tool usage
- Improved settings saving & loading logic
- Added logging for telemetry
- Removed unnecessary telemetry data

### 🐞 Fixed
- Fixed chat title generation for non-OpenAI models will fail
- Fixed web search plugin may not work in some cases
- Fixed custom model not saved or applied
- Fixed visual tree plugin is not disabled correctly

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.3...v0.3.4



## [v0.3.3](https://github.com/DearVa/Everywhere/releases/tag/v0.3.3) - 2025-10-08

### ✨ New Features
- Added telemetry to help us improve. See [Data and Privacy](https://github.com/DearVa/Everywhere/blob/main/DATA_AND_PRIVACY.md)
- Unsent messages will be saved automatically

### 🔄️ Changed
- Improved sidebar UI and animation

### 🐞 Fixed
- Fixed update message in settings page may disappear when fetching new version

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.2...v0.3.3



## [v0.3.2](https://github.com/DearVa/Everywhere/releases/tag/v0.3.2) - 2025-10-05

### 🐞 Fixed
- Fixed chat input box watermark behavior error
- (Windows) Fixed powershell plugin missing modules

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.1...v0.3.2



## [v0.3.1](https://github.com/DearVa/Everywhere/releases/tag/v0.3.1) - 2025-10-04

### 🔄️ Changed
- Improved markdown rendering styles
- Improved OOBE experience
- Changed official website link to https://everywhere.sylinko.com/

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.3.0...v0.3.1



## [v0.3.0](https://github.com/DearVa/Everywhere/releases/tag/v0.3.0) - 2025-09-24

### ✨ New Features
- 🎉 New Icon
- Added acrylic effect to tray icon menu
- Added OOBE (Out-Of-Box Experience) for first time users, including:
  - The welcome Dialog
  - Quick Setup Wizard
- Added support for custom model
- Added chat attachments storage
- Added support for more hotkeys, such as `Copilot` key on Windows
- Added watchdog process
- Chat window can be resized manually
- Chat window will show in taskbar when pinned

### 🔄️ Changed
- Refactored Plugin System, including:
  - Added Plugin Manager in Settings
  - Added file system plugin for reading and writing files
  - Added code execution plugin with PowerShell on Windows
  - Added web browsing plugin with Puppeteer
  - Added visual element plugin for capturing screen content when UI automation is not available
  - Refactored web search plugin
- Refactored logging system with structured logging
- Improved visual capturing performance
- Improved acrylic effect visibility

### 🐞 Fixed
- Fixed removing or switching chat history frequently may cause crash
- Fixed emoji rendering issues in the chat window
- Fixed application may freeze when active chat window in some cases
- Fixed settings load/save issues
- Fixed new chat button disable state is not updated when switching chat history
- Fixed detecting focused element mistakenly in some cases
- Fixed chat window may auto scroll when selecting text

### ⚠️ Known Issues
- Chat messages may disappear when selecting text
- Chat window may flicker when pinned

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.2.4...v0.3.0



## [v0.2.4](https://github.com/DearVa/Everywhere/releases/tag/v0.2.4) - 2025-08-15

### ✨ New Features
- Added Change Log in Welcome Dialog

### 🔄️ Changed
- Apply warning level filter to EF Core logging

### 🐞 Fixed
- Fixed Google Gemini invoking issues
- Fixed Restart as Administrator may not work on some cases
- Fixed Dialog and Toast may crash the app when reopen after closed a window
- Fixed `ChatElementAttachment`'s overlay window may cover the `ChatWindow`
- Fixed `ChatElementAttachment`'s overlay window may not disappear

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.2.3...v0.2.4



## [v0.2.3](https://github.com/DearVa/Everywhere/releases/tag/v0.2.3) - 2025-08-14

### ✨ New Features
- Added settings for automatically startup
- Added settings for Software Update

### 🐞 Fixed
- Fixed markdown rendering issues in the Chat Window

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.2.2...v0.2.3



## [v0.2.2](https://github.com/DearVa/Everywhere/releases/tag/v0.2.2) - 2025-08-11

### ✨ New Features
- **Model Support**: Added support for `Claude Opus 4.1`

### 🔄️ Changed
- Split settings into separate sidebar items

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.2.1...v0.2.2



## [v0.2.1](https://github.com/DearVa/Everywhere/releases/tag/v0.2.1) - 2025-08-11

### ✨ New Features
- **Model Support**: Added support for `GPT-5` series models:
  - `GPT-5`
  - `GPT-5 mini`
  - `GPT-5 nano`

### 🐞 Fixed
- Fixed markdown rendering issues in the Chat Window

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.2.0...v0.2.1



## [v0.2.0](https://github.com/DearVa/Everywhere/releases/tag/v0.2.0) - 2025-08-10

This update introduces support for over 20 new models and a completely refactored settings page for a better user experience.

### ✨ New Features

We've integrated the following new models:

- **OpenAI**: `o4-mini`, `o3`, `GPT-4.1`, `GPT-4.1 mini`, `GPT-4o` (`GPT-5` series will be released in next version)
- **Anthropic**: `Claude Opus 4`, `Claude Sonnet 4`, `Claude 3.7 Sonnet`, `Claude 3.5 Haiku`
- **Google**: `Gemini 2.5 Pro`, `Gemini 2.5 Flash`, `Gemini 2.5 Flash-Lite`
- **DeepSeek**: `DeepSeek V3`, `DeepSeek R1`
- **Moonshot**: `Kimi K2`, `Kimi Latest`, `Kimi Thinking Preview`
- **xAI**: `Grok 4`, `Grok 3 Mini`, `Grok 3`
- **Ollama**: `GPT-OSS 20B`, `DeekSeek R1 7B`, `Qwen 3 8B`

### ⚠️ BREAKING CHANGE: Database Refactor

To improve performance and stability, the chat database has been refactored.

- **As this is a beta release, chat history from previous versions is no longer available.**
- The new database structure now supports data migrations, which will prevent data loss in future updates. We appreciate your understanding.

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.1.3...v0.2.0



## [v0.1.3](https://github.com/DearVa/Everywhere/releases/tag/v0.1.3) - 2025-08-08

### ✨ New Features
- Added a pin button to the Chat Window, to keep it always on top and not close on lost focus
- Added detailed error messages in the Chat Window
- Added auto enum settings support by @SlimeNull in #10

### 🐞 Fixed
- Fixed ChatInputBox max height

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.1.2...v0.1.3



## [v0.1.2](https://github.com/DearVa/Everywhere/releases/tag/v0.1.2) - 2025-08-02

### ✨ New Features
- Added a notification when the app is first hide to the system tray

### 🔄️ Changed
- (Style) Decreased the background opacity of the main window, for Mica effect

### 🐞 Fixed
- Fixed wrong links in Welcome Dialog

### ⚠️ Known Issues
- The opacity of tray icon menu is broken

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.1.1...v0.1.2



## [v0.1.1](https://github.com/DearVa/Everywhere/releases/tag/v0.1.1) - 2025-07-31

### ✨ New Features
- Added Logging

### 🗑️ Removed
- Removed custom window corner radius (Too many bugs, not worth it)

### 🐞 Fixed
- Fixed I18N not working when Language is not set

**Full Changelog**: https://github.com/DearVa/Everywhere/compare/v0.1.0...v0.1.1



## [v0.1.0](https://github.com/DearVa/Everywhere/releases/tag/v0.1.0) - 2025-07-31

### First Release · 万物生于有，有生于无。
