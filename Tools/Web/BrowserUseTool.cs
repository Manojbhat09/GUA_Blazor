using LlmTornado.Common;
using Microsoft.Playwright;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GUA_Blazor.Tools.Web;

public class BrowserUseTool : AITool<BrowserUseArguments>
{
    protected override async Task<object?> ExecuteAsync(BrowserUseArguments args)
    {
        if (string.IsNullOrWhiteSpace(args.Action))
            throw new Exception("Action is required.");

        string actionResult;
        var browser = BrowserSession.Instance;

        try
        {
            actionResult = args.Action.ToLower() switch
            {
                "go_to_url" => await browser.GoToUrlAsync(args.Url),
                "click_element" => await browser.ClickElementAsync(args.Index),
                "input_text" => await browser.InputTextAsync(args.Index, args.Text),
                "scroll_down" => await browser.ScrollAsync(1, args.ScrollAmount),
                "scroll_up" => await browser.ScrollAsync(-1, args.ScrollAmount),
                "send_keys" => await browser.SendKeysAsync(args.Keys),
                "go_back" => await browser.GoBackAsync(),
                "refresh" => await browser.RefreshAsync(),
                "wait" => await browser.WaitAsync(args.Seconds),
                "extract_content" => await browser.ExtractContentAsync(),
                "click_coordinates" => await browser.ClickCoordinatesAsync(args.X, args.Y),
                "click_coordinates_batch" => await browser.ClickCoordinatesBatchAsync(args.Coordinates),
                _ => throw new Exception($"Unknown action: {args.Action}")
            };
        }
        catch (Exception ex)
        {
            actionResult = $"Action failed: {ex.Message}";
        }

        // For go_to_url / wait / scroll: skip full page state to save context
        bool isNavOnly = args.Action.ToLower() is "go_to_url" or "wait" or "scroll_down" or "scroll_up" or "go_back" or "refresh";
        if (isNavOnly)
        {
            return new BrowserUseOutput {
                ActionResult = actionResult + " — call extract_content to read the page.",
                BrowserState = new BrowserState { InteractiveElements = "(call extract_content first)", Instructions = "" }
            };
        }

        bool includeScreenshot = true;
        var currentState = await browser.GetCurrentStateAsync(includeScreenshot) as BrowserState;

        if (currentState is null)
            return "Error";

        var output = new BrowserUseOutput
        {
            ActionResult = actionResult,
            BrowserState = currentState
        };

        return output;
    }

    public override ToolFunction GetToolFunction() => new ToolFunction(
        "browser_use",
        "Browser automation. After go_to_url/scroll/navigation actions, call extract_content to read the page.",
        new
        {
            type = "object",
            properties = new
            {
                action = new { type = "string", @enum = new[] { "go_to_url", "click_element", "click_coordinates", "click_coordinates_batch", "input_text", "scroll_down", "scroll_up", "send_keys", "go_back", "refresh", "wait", "extract_content" } },
                url = new { type = "string" },
                index = new { type = "integer" },
                text = new { type = "string" },
                scroll_amount = new { type = "integer" },
                keys = new { type = "string" },
                seconds = new { type = "integer" },
                x = new { type = "number" },
                y = new { type = "number" },
                coordinates = new { type = "array", items = new { type = "array", items = new { type = "number" } } }
            },
            required = new List<string> { "action" }
        });
}

// 3. The Persistent Browser Session
// This ensures Playwright doesn't close between LLM tool calls.
public class BrowserSession
{
    public static readonly BrowserSession Instance = new();

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private const string BuildInteractiveElementsJs =
@"
() => {
  const ROLES = 'button,a,input,select,textarea,iframe,canvas,' +
    '[role=""button""],[role=""checkbox""],[role=""link""],[role=""menuitem""],' +
    '[role=""tab""],[role=""switch""],[role=""radio""],[role=""combobox""],' +
    '[role=""option""],[role=""listbox""],[role=""treeitem""],[role=""gridcell""],' +
    '[tabindex]:not([tabindex=""-1""])';

  const isVisible = (el, rect, style) =>
    rect.width > 2 && rect.height > 2 &&
    style.visibility !== 'hidden' &&
    style.opacity !== '0' &&
    style.display !== 'none' &&
    el.getAttribute('aria-hidden') !== 'true' &&
    !el.disabled &&
    el.getAttribute('aria-disabled') !== 'true';

  const inViewport = rect =>
    rect.top >= 0 && rect.bottom <= window.innerHeight &&
    rect.left >= 0 && rect.right <= window.innerWidth;

  const direct = Array.from(document.querySelectorAll(ROLES));
  const directSet = new Set(direct);

  const custom = Array.from(document.querySelectorAll('*')).filter(el => {
    if (directSet.has(el)) return false;
    const rect = el.getBoundingClientRect();
    const style = window.getComputedStyle(el);
    if (!isVisible(el, rect, style)) return false;
    const c = (typeof el.className === 'string') ? el.className.toLowerCase() : '';
    return style.cursor === 'pointer' ||
      c.includes('captcha') || c.includes('checkbox') || c.includes('turnstile');
  });

  const all = [...direct, ...custom].filter(el => {
    const rect = el.getBoundingClientRect();
    const style = window.getComputedStyle(el);
    return isVisible(el, rect, style);
  });

  const sorted = [
    ...all.filter(el => inViewport(el.getBoundingClientRect())),
    ...all.filter(el => !inViewport(el.getBoundingClientRect()))
  ];

  sorted.forEach((el, i) => el.setAttribute('data-browser-use-index', i));

  return sorted.map((el, i) => {
    let tag = el.tagName.toLowerCase();
    if (el.tagName === 'INPUT') tag += `[${el.getAttribute('type') || 'text'}]`;

    const cls = (typeof el.className === 'string')
      ? el.className.split(' ').filter(c => c.trim()).slice(0, 2).join('.')
      : '';
    if (cls) tag += `.${cls}`;

    let text = (el.getAttribute('aria-label') || el.getAttribute('title') ||
      el.getAttribute('placeholder') || el.value || el.innerText || '')
      .replace(/\s+/g, ' ').trim().substring(0, 40);

    if (!text) {
      let ctx = el.parentElement;
      while (ctx && ctx !== document.body) {
        const t = (ctx.innerText || '').replace(/\s+/g, ' ').trim();
        if (t) { text = `(ctx: ""${t.substring(0, 40)}"")`; break; }
        ctx = ctx.parentElement;
      }
    }

    const rect = el.getBoundingClientRect();
    const x = Math.round(rect.left + rect.width / 2);
    const y = Math.round(rect.top + rect.height / 2);
    const w = Math.round(rect.width);
    const h = Math.round(rect.height);

    return `[${i}] <${tag}> ${text || '(empty)'} [${w}x${h}] [${x},${y}]`;
  }).join('\n');
}
";

    private BrowserSession() { }

    public async Task EnsureInitializedAsync()
    {
        if (_page != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_page != null) return;

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = Environment.GetEnvironmentVariable("GUA_HEADLESS") != "false", // default headless
                Args = new[] {
                    "--disable-web-security",
                    "--disable-blink-features=AutomationControlled",
                    "--no-first-run",
                    "--no-default-browser-check"
                }
            });

            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = 720 },
                UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36",
            });

            _page = await context.NewPageAsync();

            // Stealth: remove webdriver flag and automation indicators
            await _page.AddInitScriptAsync(@"
                Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
                Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
                Object.defineProperty(navigator, 'plugins', { get: () => [1, 2, 3, 4, 5] });
                window.chrome = { runtime: {} };
            ");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<string> GoToUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new Exception("URL is required.");
        if (!url.StartsWith("http")) url = "https://" + url;

        await _page!.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Task.Delay(2000); // Give the page a moment to settle after load

        // Auto-bypass Cloudflare Turnstile if detected
        await TryBypassCloudflare();

        return $"Navigated to {url}";
    }

    private async Task TryBypassCloudflare()
    {
        try
        {
            // Check if page contains Cloudflare challenge
            var hasChallenge = await _page!.EvaluateAsync<bool>(
                "() => document.title.includes('Just a moment') || " +
                "document.querySelector('iframe[src*=\"challenges.cloudflare\"]') !== null || " +
                "document.body.innerText.includes('Verify you are human')");

            if (!hasChallenge) return;

            Console.WriteLine("[cloudflare] Turnstile challenge detected, attempting bypass...");

            // Wait for the Turnstile iframe to appear
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(2000);

                // Try clicking the Turnstile checkbox inside iframe
                var frames = _page.Frames;
                foreach (var frame in frames)
                {
                    if (frame.Url.Contains("challenges.cloudflare"))
                    {
                        try
                        {
                            // Click the checkbox inside the Turnstile iframe
                            var checkbox = frame.Locator("input[type='checkbox'], .cb-i, #challenge-stage");
                            if (await checkbox.CountAsync() > 0)
                            {
                                await checkbox.First.ClickAsync(new LocatorClickOptions { Timeout = 3000 });
                                Console.WriteLine("[cloudflare] Clicked Turnstile checkbox");
                                await Task.Delay(3000);
                                return;
                            }

                            // Try clicking by coordinates within the iframe
                            await frame.ClickAsync("body", new FrameClickOptions
                            {
                                Position = new Position { X = 24, Y = 24 },
                                Timeout = 2000
                            });
                            Console.WriteLine("[cloudflare] Clicked Turnstile iframe body");
                            await Task.Delay(3000);
                            return;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[cloudflare] Frame click attempt {attempt} failed: {ex.Message}");
                        }
                    }
                }

                // Fallback: click coordinates where Turnstile checkbox usually appears
                try
                {
                    await _page.Mouse.ClickAsync(215, 400);
                    Console.WriteLine($"[cloudflare] Fallback coordinate click attempt {attempt}");
                    await Task.Delay(3000);

                    // Check if we passed
                    var stillBlocked = await _page.EvaluateAsync<bool>(
                        "() => document.body.innerText.includes('Verify you are human')");
                    if (!stillBlocked)
                    {
                        Console.WriteLine("[cloudflare] Bypass succeeded!");
                        return;
                    }
                }
                catch { }
            }

            Console.WriteLine("[cloudflare] Could not bypass Turnstile after 3 attempts");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[cloudflare] Bypass error: {ex.Message}");
        }
    }

    public async Task<string> ClickElementAsync(int? index)
    {
        if (index == null) throw new Exception("Index is required.");

        try
        {
            var locator = _page!.Locator($"[data-browser-use-index=\"{index}\"]");

            await locator.ClickAsync(new LocatorClickOptions
            {
                Timeout = 2000,
                Force = true,
            });

            await WaitForNetworkIdle();
            return $"Clicked element [{index}] successfully.";
        }
        catch (Exception ex)
        {
            return $"Failed to click element [{index}]. The element may be inside an iframe, canvas, or re-rendered. STRATEGY: Look at the [x, y] coordinates of the element in the state and use 'click_coordinates' instead.";
        }
    }

    public async Task<string> InputTextAsync(int? index, string? text)
    {
        if (index == null || string.IsNullOrEmpty(text)) throw new Exception("Index and text are required.");
        var success = await _page!.EvaluateAsync<bool>($@"() => {{
            const el = document.querySelector(`[data-browser-use-index=""{index}""]`);
            if(el) {{ el.value = '{text}'; el.dispatchEvent(new Event('input', {{ bubbles: true }})); return true; }} return false;
        }}");

        if (!success) throw new Exception($"Element [{index}] not found.");
        return $"Input '{text}' into element [{index}]";
    }

    public async Task<string> ScrollAsync(int direction, int? scrollAmount)
    {
        int amount = scrollAmount ?? 800;
        await _page!.EvaluateAsync($"window.scrollBy(0, {direction * amount});");
        await Task.Delay(500); // Give UI time to settle
        return $"Scrolled {(direction > 0 ? "down" : "up")} by {amount} pixels.";
    }

    public async Task<string> SendKeysAsync(string? keys)
    {
        if (string.IsNullOrEmpty(keys)) throw new Exception("Keys are required.");
        await _page!.Keyboard.PressAsync(keys);
        await WaitForNetworkIdle();
        return $"Sent keys: {keys}";
    }

    public async Task<string> GoBackAsync()
    {
        await _page!.GoBackAsync();
        return "Navigated back.";
    }

    public async Task<string> RefreshAsync()
    {
        await _page!.ReloadAsync();
        return "Refreshed page.";
    }

    public async Task<string> WaitAsync(int? seconds)
    {
        int waitMs = (seconds ?? 3) * 1000;
        await Task.Delay(waitMs);
        return $"Waited {seconds ?? 3} seconds.";
    }

    public async Task<string> ExtractContentAsync(int maxChars = 1500)
    {
        var content = await _page!.EvaluateAsync<string>("() => document.body.innerText");

        if (string.IsNullOrWhiteSpace(content)) return "Page has no readable text content.";

        // Collapse excessive whitespace/newlines
        content = System.Text.RegularExpressions.Regex.Replace(content, @"\n{3,}", "\n\n");
        content = System.Text.RegularExpressions.Regex.Replace(content, @" {2,}", " ");
        content = content.Trim();

        if (content.Length <= maxChars)
            return content;

        return content[..maxChars] + $"\n\n[TRUNCATED — {content.Length - maxChars} more characters not shown. Scroll down and call extract_content again to read further.]";
    }

    public async Task<object> GetCurrentStateAsync(bool includeScreenshot = false)
    {
        if (_page == null) return new { error = "Browser not initialized." };

        await WaitForNetworkIdle();

        string interactiveElements = await _page.EvaluateAsync<string>(BuildInteractiveElementsJs);

        var lines = (interactiveElements?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? []).ToArray();

        const int cap = 15;
        interactiveElements = lines.Length > cap
            ? string.Join('\n', lines[..cap]) + $"\n... ({lines.Length - cap} more off-screen elements not shown — scroll down then extract_content to re-index)"
            : string.Join('\n', lines);

        // Detect captcha grid and get its bounding box
        var captchaInfo = await DetectCaptchaGrid();
        string instructions = "DO NOT guess indexes. Read the tags carefully. To solve custom captchas, look for an element with '.checkbox' in its tag (e.g., <div.captcha-box-checkbox>) and click its index. If you clicked the wrong thing and left the page, use the 'go_back' action.";

        if (captchaInfo != null)
        {
            instructions = captchaInfo.Instructions;
            interactiveElements += "\n\n" + captchaInfo.GridMap;
        }

        var state = new BrowserState()
        {
            Url = _page.Url,
            Title = await _page.TitleAsync(),
            InteractiveElements = interactiveElements,
            Instructions = instructions,
            ScreenshotBase64 = null,
            ScreenshotMime = null
        };

        if (includeScreenshot)
        {
            byte[] screenshotBytes;

            if (captchaInfo != null && captchaInfo.CropBox != null)
            {
                // Crop to just the captcha area for better detail
                screenshotBytes = await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Jpeg,
                    Quality = 90,
                    FullPage = false,
                    Clip = new Clip
                    {
                        X = captchaInfo.CropBox.X,
                        Y = captchaInfo.CropBox.Y,
                        Width = captchaInfo.CropBox.Width,
                        Height = captchaInfo.CropBox.Height
                    }
                });
                Console.WriteLine($"[captcha] Cropped screenshot to captcha grid ({captchaInfo.CropBox.Width}x{captchaInfo.CropBox.Height})");
            }
            else
            {
                screenshotBytes = await _page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Jpeg,
                    Quality = 80,
                    FullPage = false,
                });
            }

            state.ScreenshotMime = "image/jpeg";
            state.ScreenshotBase64 = $"data:{state.ScreenshotMime};base64,{Convert.ToBase64String(screenshotBytes)}";
        }

        return state;
    }

    private class CaptchaGridInfo
    {
        public string Instructions { get; set; } = "";
        public string GridMap { get; set; } = "";
        public CropRect? CropBox { get; set; }
    }

    private class CropRect
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    private async Task<CaptchaGridInfo?> DetectCaptchaGrid()
    {
        try
        {
            // Check for reCAPTCHA challenge iframe
            var captchaData = await _page!.EvaluateAsync<string>(@"() => {
                // Look for reCAPTCHA challenge iframe
                const rcIframes = document.querySelectorAll('iframe[src*=""recaptcha""], iframe[src*=""hcaptcha""], iframe[title*=""challenge""]');
                for (const iframe of rcIframes) {
                    const rect = iframe.getBoundingClientRect();
                    if (rect.width > 200 && rect.height > 200) {
                        return JSON.stringify({
                            type: 'recaptcha_challenge',
                            x: rect.x, y: rect.y,
                            width: rect.width, height: rect.height
                        });
                    }
                }

                // Look for reCAPTCHA anchor iframe (checkbox)
                const anchorIframes = document.querySelectorAll('iframe[src*=""anchor""], iframe[src*=""checkbox""]');
                for (const iframe of anchorIframes) {
                    const rect = iframe.getBoundingClientRect();
                    if (rect.width > 50) {
                        return JSON.stringify({
                            type: 'recaptcha_checkbox',
                            x: rect.x, y: rect.y,
                            width: rect.width, height: rect.height
                        });
                    }
                }

                // Check page text for captcha indicators
                const bodyText = document.body.innerText || '';
                if (bodyText.includes('Select all images') || bodyText.includes('select all squares')) {
                    return JSON.stringify({ type: 'captcha_text_detected' });
                }

                return null;
            }");

            if (string.IsNullOrEmpty(captchaData) || captchaData == "null")
                return null;

            var json = System.Text.Json.JsonDocument.Parse(captchaData);
            var root = json.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "recaptcha_challenge")
            {
                float ix = (float)root.GetProperty("x").GetDouble();
                float iy = (float)root.GetProperty("y").GetDouble();
                float iw = (float)root.GetProperty("width").GetDouble();
                float ih = (float)root.GetProperty("height").GetDouble();

                // The challenge iframe contains the 3x3 grid
                // Grid typically starts ~90px from top of iframe (after header)
                // Each cell is roughly (iw-20)/3 wide and similar height
                float cellW = (iw - 20) / 3;
                float cellH = cellW; // cells are square
                float gridStartX = ix + 10;
                float gridStartY = iy + 100; // skip the header
                float verifyY = iy + ih - 40; // verify button near bottom
                float verifyX = ix + iw - 60;

                var gridMap = new System.Text.StringBuilder();
                gridMap.AppendLine("[CAPTCHA GRID DETECTED - 3x3 image grid]");
                gridMap.AppendLine("Use click_coordinates_batch to select ALL matching images + VERIFY in ONE action.");
                gridMap.AppendLine("Grid cell centers (use these exact coordinates):");

                int cellNum = 1;
                for (int row = 0; row < 3; row++)
                {
                    for (int col = 0; col < 3; col++)
                    {
                        float cx = gridStartX + col * cellW + cellW / 2;
                        float cy = gridStartY + row * cellH + cellH / 2;
                        gridMap.AppendLine($"  Cell {cellNum} (row {row+1}, col {col+1}): [{(int)cx}, {(int)cy}]");
                        cellNum++;
                    }
                }
                gridMap.AppendLine($"  VERIFY button: [{(int)verifyX}, {(int)verifyY}]");
                gridMap.AppendLine("IMPORTANT: Analyze the cropped screenshot. Pick ALL cells containing the target object. Include VERIFY at the end.");

                return new CaptchaGridInfo
                {
                    Instructions = $"CAPTCHA IMAGE GRID DETECTED! The screenshot shows ONLY the captcha area (cropped for detail). Select ALL matching images using click_coordinates_batch with the cell coordinates listed below, plus the VERIFY button. Act in ONE action - the challenge times out quickly!",
                    GridMap = gridMap.ToString(),
                    CropBox = new CropRect
                    {
                        X = Math.Max(0, ix - 5),
                        Y = Math.Max(0, iy - 5),
                        Width = Math.Min(iw + 10, 1280 - ix),
                        Height = Math.Min(ih + 10, 720 - iy)
                    }
                };
            }
            else if (type == "recaptcha_checkbox")
            {
                float ix = (float)root.GetProperty("x").GetDouble();
                float iy = (float)root.GetProperty("y").GetDouble();
                float iw = (float)root.GetProperty("width").GetDouble();
                float ih = (float)root.GetProperty("height").GetDouble();

                float checkboxX = ix + 25;
                float checkboxY = iy + ih / 2;

                return new CaptchaGridInfo
                {
                    Instructions = $"reCAPTCHA checkbox detected. Click coordinates [{(int)checkboxX}, {(int)checkboxY}] to activate it.",
                    GridMap = $"[reCAPTCHA CHECKBOX at [{(int)checkboxX}, {(int)checkboxY}] - click it with click_coordinates]",
                    CropBox = null // don't crop for checkbox
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[captcha] Detection error: {ex.Message}");
            return null;
        }
    }


    public async Task<byte[]?> GetScreenshotBytesAsync()
    {
        if (_page == null) return null;
        try
        {
            return await _page.ScreenshotAsync(new PageScreenshotOptions
            {
                Type = ScreenshotType.Jpeg,
                Quality = 85,
                FullPage = false,
            });
        }
        catch { return null; }
    }

    private async Task WaitForNetworkIdle()
    {
        try
        {
            await _page!.WaitForLoadStateAsync((LoadState?)WaitUntilState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 3000 });
        }
        catch { /* Ignore timeout if network isn't completely idle */ }
    }

    public async Task<string> ClickCoordinatesAsync(double? x, double? y)
    {
        if (x == null || y == null) throw new Exception("X and Y coordinates are required.");

        try
        {
            await _page!.Mouse.MoveAsync((float)x, (float)y, new MouseMoveOptions { Steps = 5 });
            await Task.Delay(Random.Shared.Next(100, 250));

            await _page!.Mouse.DownAsync();

            await Task.Delay(Random.Shared.Next(50, 150));

            await _page!.Mouse.UpAsync();

            await WaitForNetworkIdle();
            return $"Successfully clicked coordinates X:{x}, Y:{y}";
        }
        catch (Exception ex)
        {
            return $"Failed to click coordinates: {ex.Message}";
        }
    }

    public async Task<string> ClickCoordinatesBatchAsync(List<List<double>>? coordinates)
    {
        if (coordinates == null || coordinates.Count == 0)
            throw new Exception("Coordinates list is required and must not be empty.");

        var results = new List<string>();
        foreach (var coord in coordinates)
        {
            if (coord.Count < 2)
            {
                results.Add("Skipped invalid coordinate (need [x, y])");
                continue;
            }

            double cx = coord[0];
            double cy = coord[1];

            try
            {
                await _page!.Mouse.MoveAsync((float)cx, (float)cy, new MouseMoveOptions { Steps = 3 });
                await Task.Delay(Random.Shared.Next(80, 200));
                await _page!.Mouse.DownAsync();
                await Task.Delay(Random.Shared.Next(40, 120));
                await _page!.Mouse.UpAsync();
                await Task.Delay(Random.Shared.Next(200, 500));
                results.Add($"Clicked ({cx}, {cy})");
            }
            catch (Exception ex)
            {
                results.Add($"Failed ({cx}, {cy}): {ex.Message}");
            }
        }

        await WaitForNetworkIdle();
        return $"Batch clicked {results.Count} coordinates: {string.Join("; ", results)}";
    }
}

public class BrowserUseArguments
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("index")]
    public int? Index { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("scroll_amount")]
    public int? ScrollAmount { get; set; }

    [JsonPropertyName("keys")]
    public string? Keys { get; set; }

    [JsonPropertyName("seconds")]
    public int? Seconds { get; set; }

    [JsonPropertyName("x")]
    public double? X { get; set; }

    [JsonPropertyName("y")]
    public double? Y { get; set; }

    [JsonPropertyName("coordinates")]
    public List<List<double>>? Coordinates { get; set; }
}

public sealed class BrowserUseOutput
{
    [JsonPropertyName("action_result")]
    public string? ActionResult { get; set; }

    [JsonPropertyName("browser_state")]
    public BrowserState? BrowserState { get; set; }
}

public sealed class BrowserState
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("interactive_elements_map")]
    public string InteractiveElements { get; set; }

    // ha base64-ben jön
    [JsonPropertyName("screenshot_base64")]
    public string? ScreenshotBase64 { get; set; }

    [JsonPropertyName("screenshot_mime")]
    public string? ScreenshotMime { get; set; } = "image/jpeg";
}