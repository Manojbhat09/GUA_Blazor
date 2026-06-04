using GUA_Blazor.Helper;
using GUA_Blazor.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;
using GUA_Blazor.Tools;
using GUA_Blazor.Tools.Filesystem;
using GUA_Blazor.Tools.Terminal;
using GUA_Blazor.Tools.TTS;
using GUA_Blazor.Tools.Web;
using GUA_Blazor.Tools.WhisperTools;
using LlmTornado;
using LlmTornado.Chat;
using LlmTornado.ChatFunctions;
using LlmTornado.Code;
using LlmTornado.Common;
using LlmTornado.Images;
using LlmTornado.Moderation;

namespace GUA_Blazor.Service;

public class AIService
{
    private TornadoApi _api; 
    private Conversation _conversation;
    private readonly TerminalSessionStore _sessionStore;
    private readonly Dictionary<string, IAITool> _tools;
    private string _sessionId;
    private readonly string _modelName;
    private int _screenshotCounter;
    private bool _supportsImages = Environment.GetEnvironmentVariable("GUA_SUPPORTS_IMAGES") == "true";
    private static readonly int _maxTokens = int.TryParse(Environment.GetEnvironmentVariable("GUA_MAX_TOKENS"), out var mt) ? mt : 4096;

    // Context compression thresholds (token estimates)
    private const int IMAGE_STRIP_THRESHOLD = 20_000;
    private const int COMPACT_THRESHOLD = 30_000;
    private const int TARGET_BUDGET = 8_000;

    private static readonly HashSet<string> ImageExtensions = new()
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp"
    };

    private static readonly HashSet<string> TextExtensions = new()
    {
        ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml",
        ".html", ".htm", ".css", ".js", ".ts", ".cs", ".py",
        ".java", ".cpp", ".c", ".h", ".sql", ".log", ".ini", ".config"
    };

    private static readonly HashSet<string> VideoExtensions = new()
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm"
    };

    private static readonly HashSet<string> AudioExtensions = new()
    {
        ".mp3", ".wav", ".ogg", ".flac", ".m4a"
    };

    public AIService(string sessionId)
    {
        _sessionId = sessionId;
        var endpoint = Environment.GetEnvironmentVariable("GUA_API_ENDPOINT") ?? "http://localhost:8080/";
        var apiKey = Environment.GetEnvironmentVariable("GUA_API_KEY") ?? "not-needed";
        _modelName = Environment.GetEnvironmentVariable("GUA_MODEL") ?? "deepseek-chat";
        _api = new TornadoApi(new Uri(endpoint), apiKey);
        _conversation = _api.Chat.CreateConversation(new ChatRequest()
        {
            Messages = [
                new LlmTornado.Chat.ChatMessage(ChatMessageRoles.System, Instructions.BasicInstruction)
            ],
            ToolChoice = OutboundToolChoice.None,
            ParallelToolCalls = true,
            Model = _modelName,
            MaxTokens = _maxTokens
        });

        _sessionStore = new();
        _tools = new Dictionary<string, IAITool>
        {
            ["create_file"] = new CreateFile(_sessionId),
            ["read_file"] = new ReadFile(_sessionId),
            ["edit_file"] = new EditFile(_sessionId),
            ["delete_file"] = new DeleteFile(_sessionId),
            ["move_file"] = new MoveFile(_sessionId),
            ["rename_file"] = new RenameFile(_sessionId),
            ["search_in_files"] = new SearchInFiles(_sessionId),
            ["list_directory"] = new ListDirectory(_sessionId),
            ["run_command"] = new RunCommand(_sessionStore, _sessionId),
            ["read_terminal_output"] = new ReadTerminalOutput(_sessionStore),
            ["stop_loop"] = new StopTool(),
            ["extract_audio"] = new ExtractAudio(_sessionId),
            ["transcribe_audio"] = new TranscribeAudio(_sessionId),
            ["burn_subtitles"] = new BurnSubtitles(_sessionId),
            ["text_to_speech"] = new TextToSpeech(_sessionId),
            ["send_voice_message"] = new SendVoiceMessage(_sessionId),
            ["merge_audio"] = new MergeAudio(_sessionId),
            ["merge_audio_with_video"] = new MergeAudioWithVideo(_sessionId),
            ["web_search"] = new WebSearch(),
            ["scrape_url"] = new ScrapeUrl(),
            ["zip_directory"] = new ZipDirectory(_sessionId),
            ["unzip_file"] = new UnzipFile(_sessionId),
            ["create_pdf"] = new CreatePdf(_sessionId),
            ["git_ingest"] = new GitIngestTool(),
            ["browser_use"] = new BrowserUseTool(),
            ["vision_detect"] = new VisionDetectTool(),
            ["ask_user"] = new AskUser(),
        };
    }

    public Task SendMessageWithImage(string message, List<string> imagesPath, Action<string> onResponse, CancellationToken ct = default)
    {
        ChatMessagePart[] parts = ResolveImages(imagesPath);
        parts[0] = new ChatMessagePart(message);

        var messages = new List<LlmTornado.Chat.ChatMessage>();
        messages.Add(new(ChatMessageRoles.System, Instructions.BasicInstruction));
        messages.AddRange(
            _conversation.Messages
                .Where(m => m.Role != ChatMessageRoles.System && m.Name != "stop_loop")
        );
        messages.Add(new(ChatMessageRoles.User, parts));

        _conversation = _api.Chat.CreateConversation(new ChatRequest()
        {
            Messages = messages,
            ToolChoice = OutboundToolChoice.None,
            Model = _modelName,
            MaxTokens = _maxTokens
        });

        return _conversation.StreamResponseRich(CreateHandler(onResponse, _conversation, null), ct);
    }

    public async Task SendMessageAgent(string message, List<string> imagesPath, Action<string> onResponse, CancellationToken ct = default)
    {
        bool isSlimModel = _modelName.Contains("LFM") || _modelName.Contains("lfm") || _modelName.Contains("Tool") || _modelName.Contains("slim") || _modelName.Contains("qwen") || _modelName.Contains("Qwen");
        int MaxTurns = isSlimModel ? 15 : 50;

        var parts = ResolveImages(imagesPath);
        parts[0] = new ChatMessagePart(message);

        var messages = new List<LlmTornado.Chat.ChatMessage>
        {
            new(ChatMessageRoles.System, isSlimModel ? Instructions.SlimAgentInstruction : Instructions.AgentInstruction)
        };
        messages.AddRange(_conversation.Messages
            .Where(m => m.Role != ChatMessageRoles.System && m.Name != "stop_loop"));
        messages.Add(new(ChatMessageRoles.User, parts));

        _conversation = _api.Chat.CreateConversation(new ChatRequest
        {
            Tools = (isSlimModel
                ? _tools.Where(t => t.Key is "browser_use" or "vision_detect" or "stop_loop" or "create_file" or "read_file" or "edit_file" or "list_directory" or "run_command" or "read_terminal_output" or "extract_audio" or "web_search" or "scrape_url" or "ask_user")
                         .Select(t => new Tool(t.Value.GetToolFunction())).ToList()
                : _tools.Values.Select(t => new Tool(t.GetToolFunction())).ToList()),
            Messages = messages,
            InvokeClrToolsAutomatically = false,
            ToolChoice = OutboundToolChoice.Required,
            ParallelToolCalls = false,
            Model = _modelName,
            MaxTokens = _maxTokens
        });

        var stopSignal = new StopSignal();
        var handler = CreateHandler(onResponse, _conversation, stopSignal);
        int idleCount = 0;
        const int IdleLimit = 5;

        for (int turn = 0; turn < MaxTurns && !stopSignal.Stop && !ct.IsCancellationRequested; turn++)
        {
            var lastMessage = _conversation.Messages.LastOrDefault();
            bool needsPrompt = turn > 0 && lastMessage?.Role != ChatMessageRoles.Tool;

            if (needsPrompt)
            {
                int remaining = MaxTurns - turn;
                string planContext = turn > 0 ? BuildPlanInjection() : "";

                string nudge;
                if (remaining <= 3)
                    nudge = $"[Turn {turn + 1}/{MaxTurns}] URGENT: Only {remaining} turns left. Wrap up and call stop_loop now.";
                else if (!string.IsNullOrEmpty(planContext))
                    nudge = $"[Turn {turn + 1}/{MaxTurns}]\n{planContext}\nWork on the next unchecked step. Call stop_loop when done.";
                else if (turn == 1)
                    nudge = $"[Turn {turn + 1}/{MaxTurns}] Reminder: create planning/task_plan.md first if this is a multi-step task. Call stop_loop when done.";
                else
                    nudge = $"[Turn {turn + 1}/{MaxTurns}] Continue your task. Call stop_loop when done.";

                _conversation.AppendUserInputWithName("agent_helper", nudge);
            }

            var msgCountBefore = _conversation.Messages.Count;
            await _conversation.StreamResponseRich(handler, ct);

            // Detect idle: if model only produced text with no tool calls
            bool hadToolCall = false;
            for (int mi = msgCountBefore; mi < _conversation.Messages.Count; mi++)
            {
                if (_conversation.Messages[mi].Role == ChatMessageRoles.Tool)
                {
                    hadToolCall = true;
                    break;
                }
            }
            if (!hadToolCall && turn > 0)
            {
                idleCount++;
                Console.WriteLine($"[agent] Idle turn {idleCount}/{IdleLimit} (no tool calls)");
                if (idleCount >= IdleLimit)
                {
                    Console.WriteLine("[agent] Idle limit reached, stopping loop");
                    break;
                }
            }
            else
            {
                idleCount = 0;
            }

            // Context compression between turns
            await CompressIfNeeded(message);

            onResponse("//NEW_TURN//");
        }
    }

    private ChatStreamEventHandler CreateHandler(Action<string> onResponse, Conversation conversation, StopSignal stopSignal)
    {
        return new ChatStreamEventHandler
        {
            MessageTokenHandler = async (x) =>
            {
                onResponse?.Invoke(x!);
            },
            FunctionCallHandler = async (calls) =>
            {
                await ResolveFunctions(calls, onResponse, conversation);
            },
            AfterFunctionCallsResolvedHandler = async (results, currentHandler) =>
            {
                foreach (var result in results.ToolResults)
                {
                    if (result?.Result?.Name == "stop_loop" && result?.Result?.Content == "stopping_loop")
                    {
                        if (stopSignal != null) stopSignal.Stop = true;
                    }

                    if (result?.Result?.Name == "ask_user" && result?.Result?.Content is string content && content.StartsWith("[USER_PROMPT_REQUIRED]"))
                    {
                        if (stopSignal != null) stopSignal.Stop = true;
                    }
                }
            }
        };
    }

    private async Task ResolveFunctions(List<FunctionCall> calls, Action<string> onResponse, Conversation conversation)
    {
        foreach (var call in calls)
        {
            if (onResponse != null)
                onResponse.Invoke($"//TOOLCALL\n{call?.ToolCall?.FunctionCall?.GetJson()}\n//TOOLCALL_END");

            if (_tools.TryGetValue(call!.Name, out var tool))
            {
                try
                {
                    var result = await tool.ExecuteFunctionAsync(call);

                    if (result is string resultString)
                    {
                        if (resultString!.Contains("[VOICE_MESSAGE_SENT]"))
                            onResponse.Invoke(result.ToString() + "\n");
                    }

                    if (result is BrowserUseOutput buo)
                    {
                        StringWriter sw = new();
                        await sw.WriteLineAsync($"[RESULT]: {buo.ActionResult}");
                        await sw.WriteLineAsync($"[INTERACTIVE ELEMENTS]: {buo.BrowserState?.InteractiveElements ?? "No clickables try extracting"}");
                        await sw.WriteLineAsync($"[INSTRUCTIONS]: {buo.BrowserState?.Instructions ?? "No instructions"}");

                        call.Result = new FunctionResult(call, sw.ToString(), null);
                        if (buo.BrowserState?.ScreenshotBase64 is { Length: > 0 } b64)
                        {
                            if (_supportsImages)
                            {
                                conversation.AppendMessage(new LlmTornado.Chat.ChatMessage(ChatMessageRoles.User, [
                                    new ChatMessagePart(b64, ImageDetail.Auto),
                                    new ChatMessagePart($"[Browser state screenshot attached. You can use this to inform your next actions.]"),
                                ])
                                {
                                    Name = "agent_helper"
                                });
                            }
                            else
                            {
                                conversation.AppendMessage(new LlmTornado.Chat.ChatMessage(ChatMessageRoles.User,
                                    "[Browser screenshot taken but model is text-only. Use vision_detect tool to analyze page visually, or extract_content for text.]")
                                {
                                    Name = "agent_helper"
                                });
                            }

                            // Save screenshot to disk cache and notify UI
                            var ssDir = Path.Combine("/tmp/gua_screenshots", _sessionId);
                            Directory.CreateDirectory(ssDir);
                            var ssIdx = System.Threading.Interlocked.Increment(ref _screenshotCounter);
                            var ssPath = Path.Combine(ssDir, $"{ssIdx}.jpg");
                            var commaIdx = b64.IndexOf(',');
                            if (commaIdx > 0)
                            {
                                File.WriteAllBytes(ssPath, Convert.FromBase64String(b64.Substring(commaIdx + 1)));
                                onResponse?.Invoke($"//SCREENSHOT_FILE\n/screenshots/{_sessionId}/{ssIdx}.jpg\n//SCREENSHOT_FILE_END");
                            }
                        }
                    }
                    else
                    {
                        call.Result = new FunctionResult(call, result, null);
                    }
                } catch (Exception e)
                {
                    // Auto-detect: if server returns "image input is not supported", disable images
                    if (e.Message.Contains("image input is not supported") || e.Message.Contains("mmproj"))
                    {
                        _supportsImages = false;
                        Console.WriteLine("[agent] Model does not support images (no mmproj). Disabling screenshot injection.");
                    }
                    call.Result = new FunctionResult(call, e.Message, false);
                }
            }
            else
            {
                call.Result = new FunctionResult(call, "Function not found", false);
            }
        }
    }

    private ChatMessagePart[] ResolveImages(List<string> filePaths)
    {
        var parts = new List<ChatMessagePart> { null! };

        foreach (var filePath in filePaths)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var fileName = Path.GetFileName(filePath);

            if (ImageExtensions.Contains(ext))
            {
                var mimeType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                byte[] bytes = File.ReadAllBytes(filePath);
                string dataUrl = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
                parts.Add(new ChatMessagePart(dataUrl, ImageDetail.High));
            }
            else if (TextExtensions.Contains(ext))
            {
                var content = File.ReadAllText(filePath);
                parts.Add(new ChatMessagePart(
                    $"[Attached file: {fileName}]\n```\n{content}\n```"));
            }
            else if (ext == ".pdf")
            {
                var text = ExtractPdfText(filePath);
                parts.Add(new ChatMessagePart($"[Attached PDF: {fileName}]\n{text}"));
            }
            else if (VideoExtensions.Contains(ext) || AudioExtensions.Contains(ext))
            {
                // Tell the agent the file exists and its path so it can call extract_audio / transcribe_audio
                parts.Add(new ChatMessagePart(
                    $"[Attached {(VideoExtensions.Contains(ext) ? "video" : "audio")} file: {fileName}]\n" +
                    $"Full path: {filePath}\n" +
                    $"You can transcribe this using extract_audio (if video) then transcribe_audio, " +
                    $"and burn subtitles with burn_subtitles."));
            }
            else
            {
                parts.Add(new ChatMessagePart(
                    $"[Attached file: {fileName} — binary format, content not available]"));
            }
        }

        return parts.ToArray();
    }

    private static string ExtractPdfText(string path)
    {
        var sb = new System.Text.StringBuilder();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return sb.ToString();
    }

    // ==================== Context Compression ====================

    private async Task CompressIfNeeded(string currentQuery)
    {
        var tokenEstimate = EstimateTokenCount(_conversation.Messages);
        Console.WriteLine($"[compress] Token estimate: {tokenEstimate}");

        if (tokenEstimate > IMAGE_STRIP_THRESHOLD)
        {
            int stripped = StripOldImages(keepLast: 2);
            if (stripped > 0)
            {
                Console.WriteLine($"[compress] Stage 1: Stripped {stripped} images");
                tokenEstimate = EstimateTokenCount(_conversation.Messages);
                Console.WriteLine($"[compress] After strip: {tokenEstimate} tokens");
            }
        }

        if (tokenEstimate > COMPACT_THRESHOLD)
        {
            Console.WriteLine($"[compress] Stage 2: Calling ctxpact (budget={TARGET_BUDGET})...");
            try
            {
                var compressed = await CompressViaCtxpact(
                    _conversation.Messages, currentQuery, TARGET_BUDGET);

                if (!string.IsNullOrEmpty(compressed))
                {
                    RebuildConversation(compressed, keepLastN: 4);
                    var newEstimate = EstimateTokenCount(_conversation.Messages);
                    Console.WriteLine($"[compress] After ctxpact: {newEstimate} tokens");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[compress] ctxpact failed: {ex.Message}");
                // Fall back to aggressive image stripping
                StripOldImages(keepLast: 1);
            }
        }
    }

    private static int EstimateTokenCount(IReadOnlyList<LlmTornado.Chat.ChatMessage> messages)
    {
        int tokens = 0;
        foreach (var msg in messages)
        {
            if (msg.Parts != null)
            {
                foreach (var part in msg.Parts)
                {
                    if (part.Image != null || (part.Text != null && part.Text.StartsWith("data:image/")))
                        tokens += 1500; // ~1500 tokens per vision image
                    else if (part.Text != null)
                        tokens += part.Text.Length / 4;
                }
            }
            else if (msg.Content != null)
            {
                if (msg.Content.Contains("data:image/"))
                    tokens += msg.Content.Length / 3;
                else
                    tokens += msg.Content.Length / 4;
            }
            tokens += 4; // message overhead
        }
        return tokens;
    }

    private int StripOldImages(int keepLast)
    {
        int stripped = 0;
        var messages = _conversation.Messages.ToList();

        // Find messages with images
        var imageIndices = new List<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            bool hasImage = false;
            if (msg.Parts != null)
                hasImage = msg.Parts.Any(p =>
                    p.Image != null ||
                    (p.Text != null && p.Text.StartsWith("data:image/")));
            else if (msg.Content != null)
                hasImage = msg.Content.Contains("data:image/");

            if (hasImage) imageIndices.Add(i);
        }

        // Strip all but last keepLast image messages
        int toStrip = imageIndices.Count - keepLast;
        var newMessages = new List<LlmTornado.Chat.ChatMessage>();
        var stripSet = new HashSet<int>();
        for (int j = 0; j < toStrip && j < imageIndices.Count; j++)
            stripSet.Add(imageIndices[j]);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (stripSet.Contains(i))
            {
                // Replace image message with text-only version
                if (msg.Parts != null)
                {
                    var textParts = msg.Parts
                        .Where(p => p.Image == null && !(p.Text != null && p.Text.StartsWith("data:image/")))
                        .Select(p => p.Text ?? "")
                        .ToList();
                    textParts.Add("[Screenshot removed - see agent description]");
                    var replacement = new LlmTornado.Chat.ChatMessage(msg.Role ?? ChatMessageRoles.User, string.Join("\n", textParts));
                    if (msg.Name != null) replacement.Name = msg.Name;
                    newMessages.Add(replacement);
                    stripped++;
                }
                else if (msg.Content != null && msg.Content.Contains("data:image/"))
                {
                    var cleanContent = Regex.Replace(msg.Content,
                        @"data:image/[^;]+;base64,[A-Za-z0-9+/=]+",
                        "[Screenshot removed - see agent description]");
                    var replacement = new LlmTornado.Chat.ChatMessage(msg.Role ?? ChatMessageRoles.User, cleanContent);
                    if (msg.Name != null) replacement.Name = msg.Name;
                    newMessages.Add(replacement);
                    stripped++;
                }
                else
                {
                    newMessages.Add(msg);
                }
            }
            else
            {
                newMessages.Add(msg);
            }
        }

        if (stripped > 0)
        {
            // Rebuild conversation with stripped messages
            _conversation = _api.Chat.CreateConversation(new ChatRequest
            {
                Tools = _conversation.Messages.Any(m => m.Role == ChatMessageRoles.Tool)
                    ? _tools.Values.Select(t => new Tool(t.GetToolFunction())).ToList()
                    : null,
                Messages = newMessages,
                InvokeClrToolsAutomatically = false,
                ToolChoice = OutboundToolChoice.Required,
                ParallelToolCalls = false,
                Model = _modelName,
                MaxTokens = _maxTokens
            });
        }
        return stripped;
    }

    private async Task<string> CompressViaCtxpact(
        IReadOnlyList<LlmTornado.Chat.ChatMessage> messages,
        string currentQuery,
        int tokenBudget)
    {
        // Serialize messages to OpenAI format (text only, no images)
        var serialized = new List<object>();
        foreach (var msg in messages)
        {
            string role = msg.Role?.ToString()?.ToLower() ?? "user";
            string text = "";
            if (msg.Parts != null)
            {
                text = string.Join("\n", msg.Parts
                    .Where(p => p.Text != null && !p.Text.StartsWith("data:image/"))
                    .Select(p => p.Text));
            }
            else
            {
                text = msg.Content ?? "";
            }
            // Skip empty messages and very short ones
            if (text.Length > 10)
                serialized.Add(new { role, content = text });
        }

        var input = System.Text.Json.JsonSerializer.Serialize(new
        {
            messages = serialized,
            query = currentQuery,
            token_budget = tokenBudget,
            provider_url = Environment.GetEnvironmentVariable("GUA_API_ENDPOINT")?.TrimEnd('/') + "/v1" ?? "http://localhost:8081/v1",
            model = _modelName
        });

        var psi = new ProcessStartInfo
        {
            FileName = "/opt/mlx-env/bin/python",
            Arguments = $"{Environment.GetEnvironmentVariable("HOME")}/ctxpact/compress_context.py",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception("Failed to start ctxpact process");

        await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();

        var output = await process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (!string.IsNullOrEmpty(errors))
            Console.WriteLine($"[compress] ctxpact stderr: {errors[..Math.Min(errors.Length, 500)]}");

        if (process.ExitCode != 0)
            throw new Exception($"ctxpact exited with code {process.ExitCode}");

        var result = System.Text.Json.JsonDocument.Parse(output);
        var compressed = result.RootElement.GetProperty("compressed").GetString();
        var origTokens = result.RootElement.GetProperty("original_tokens").GetInt32();
        var compTokens = result.RootElement.GetProperty("compressed_tokens").GetInt32();

        Console.WriteLine($"[compress] ctxpact: {origTokens} -> {compTokens} tokens");
        return compressed ?? "";
    }

    private void RebuildConversation(string compressedHistory, int keepLastN)
    {
        var oldMessages = _conversation.Messages.ToList();

        // Keep system prompt
        var systemMsg = oldMessages.FirstOrDefault(m => m.Role == ChatMessageRoles.System);

        // Keep last N non-system messages
        var nonSystem = oldMessages.Where(m => m.Role != ChatMessageRoles.System).ToList();
        var recentMessages = nonSystem.Skip(Math.Max(0, nonSystem.Count - keepLastN)).ToList();

        // Rebuild
        var newMessages = new List<LlmTornado.Chat.ChatMessage>();
        if (systemMsg != null) newMessages.Add(systemMsg);

        // Add compressed context as a user message
        newMessages.Add(new LlmTornado.Chat.ChatMessage(ChatMessageRoles.User,
            $"=== Compressed Context (previous conversation) ===\n{compressedHistory}\n=== End Compressed Context ===")
        {
            Name = "context_summary"
        });

        // Add recent messages
        newMessages.AddRange(recentMessages);

        // Create new conversation with compressed messages
        _conversation = _api.Chat.CreateConversation(new ChatRequest
        {
            Tools = _tools.Values.Select(t => new Tool(t.GetToolFunction())).ToList(),
            Messages = newMessages,
            InvokeClrToolsAutomatically = false,
            ToolChoice = OutboundToolChoice.Required,
            ParallelToolCalls = false,
            Model = _modelName,
            MaxTokens = _maxTokens
        });
    }

    // ==================== Plan File Injection ====================

    private string BuildPlanInjection()
    {
        try
        {
            var workPath = SessionSandbox.GetWorkPath(_sessionId);
            var planDir = Path.Combine(workPath, "planning");
            var taskPlanPath = Path.Combine(planDir, "task_plan.md");
            var progressPath = Path.Combine(planDir, "progress.md");
            var findingsPath = Path.Combine(planDir, "findings.md");

            if (!File.Exists(taskPlanPath))
                return "";

            var sb = new System.Text.StringBuilder();

            // task_plan.md: extract goal + checkbox lines
            var lines = File.ReadAllLines(taskPlanPath);
            string goalLine = null;
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("## Goal"))
                {
                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var next = lines[j].Trim();
                        if (!string.IsNullOrEmpty(next) && !next.StartsWith("#"))
                        {
                            goalLine = next;
                            break;
                        }
                    }
                }
                if (trimmed.StartsWith("- ["))
                    sb.AppendLine(trimmed);
            }

            if (goalLine != null)
                sb.Insert(0, $"Goal: {goalLine}\nPlan:\n");
            else
                sb.Insert(0, "Plan:\n");

            // progress.md: last 5 lines
            if (File.Exists(progressPath))
            {
                var pLines = File.ReadAllLines(progressPath);
                var skip = Math.Max(0, pLines.Length - 5);
                sb.AppendLine("\nRecent progress:");
                for (int i = skip; i < pLines.Length; i++)
                    sb.AppendLine(pLines[i]);
            }

            // findings.md: last 3 lines
            if (File.Exists(findingsPath))
            {
                var fLines = File.ReadAllLines(findingsPath);
                var skip = Math.Max(0, fLines.Length - 3);
                sb.AppendLine("\nKey findings:");
                for (int i = skip; i < fLines.Length; i++)
                    sb.AppendLine(fLines[i]);
            }

            var result = sb.ToString();
            if (result.Length > 600)
                result = result.Substring(0, 597) + "...";

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[planning] BuildPlanInjection error: {ex.Message}");
            return "";
        }
    }
}

public class StopSignal
{
    public bool Stop { get; set; } = false;
}