using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RagPipeline.Embeddings;      // ✅ VoyageEmbedder
using RagPipeline.VectorDb;

namespace RagPipeline.Services
{
    public class RagResponse
    {
        public string Answer { get; set; } = "";
        public List<RagSourceDoc> Sources { get; set; } = new List<RagSourceDoc>();
    }

    public class RagSourceDoc
    {
        public string FileName { get; set; } = "";
        public string Page { get; set; } = "";
        public string Content { get; set; } = "";
        public double Score { get; set; }
    }
    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }
    public class RagQueryService
    {
        private readonly VoyageEmbedder _embedder;   // ✅ Voyage embedder
        private readonly QdrantIndexer _indexer;
        private readonly HttpClient _groqClient;
        private readonly string _groqModel = "llama-3.3-70b-versatile";


        public RagQueryService(VoyageEmbedder embedder, QdrantIndexer indexer)  // ✅ 接收 VoyageEmbedder
        {
            _embedder = embedder;
            _indexer = indexer;

            var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new Exception("環境變數 GROQ_API_KEY 未設定。");

            _groqClient = new HttpClient
            {
                BaseAddress = new Uri("https://api.groq.com/openai/v1/"),
                Timeout = TimeSpan.FromSeconds(100)
            };

            _groqClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        public async Task<RagResponse> AskAsync(string query, List<ChatMessage> history = null, int topK = 5)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new RagResponse { Answer = "請輸入查詢內容。" }; // 修正回傳格式
            history ??= new List<ChatMessage>();

            // ============================================================
            // 🧠 0. 查詢改寫 (Memory Refinement)
            // ============================================================
            string refinedQuery = query;
            if (history.Any())
            {
                var lastUserMessage = history.LastOrDefault(m => m.Role == "user");
                if (lastUserMessage != null)
                {
                    refinedQuery = $"{lastUserMessage.Content} {query}";
                    Console.WriteLine($"\n🧠 [記憶啟動] 上下文合併查詢: {refinedQuery}");
                }
            }
            else
            {
                Console.WriteLine($"\n🔍 [AskAsync] 查詢: {query}");
            }

            // ============================================================
            // 🚀 1. 智慧意圖偵測 & 參數設定
            // ============================================================
            var wideScopeKeywords = new[]
            {
        "流程", "步驟", "程序", "辦法",
        "所有", "全部", "清單", "列表", "有哪些",
        "比較", "差異", "如何", "怎麼", "方式",
        "公式", "計算", "評估", "評分", "評價", "標準", "等級", "定義", "指標",
        "時間", "小時", "分鐘", "多久", "頻率", "幾次"
    };

            // 判斷是否需要廣域檢索 (SOP 模式)
            bool needsMoreContext = wideScopeKeywords.Any(k => refinedQuery.Contains(k));

            // ⚠️ 策略調整：兩階段檢索 (為了省 Token 並抓到附錄)
            // Qdrant 抓取量 (Fetch): 故意抓很多，確保附錄表格有進來
            int fetchCount = needsMoreContext ? 40 : 15;

            // LLM 送出量 (Send): 只送最精華的前幾名，避免爆 TPD 限制
            int finalSendCount = needsMoreContext ? 5 : 3; // 5 筆 PDF 整頁約 3500 tokens，非常安全

            Console.WriteLine($"   👉 模式: {(needsMoreContext ? "廣域統整 (SOP Mode)" : "精準搜尋 (Fact Mode)")}");
            Console.WriteLine($"   👉 策略: Qdrant 抓取 {fetchCount} 筆 -> 重排序 -> LLM 接收 {finalSendCount} 筆");

            // ============================================================
            // 📡 2. 向量化 & 初步檢索
            // ============================================================
            var queryEmbedding = await _embedder.EmbedAsync(refinedQuery);
            var rawResults = await _indexer.SearchAsync(queryEmbedding, fetchCount);

            if (!rawResults.Any())
            {
                return new RagResponse
                {
                    Answer = "在知識庫中找不到相關內容。",
                    Sources = new List<RagSourceDoc>() // 來源為空
                };
            }

            // ============================================================
            // ⚖️ 3. 本地關鍵字重排序 (Context-Aware Reranking)
            // ============================================================

            // A. 拆解使用者關鍵字
            var userKeywords = refinedQuery.Split(new[] { ' ', '?', '。', '，' }, StringSplitOptions.RemoveEmptyEntries)
                                           .Where(k => k.Length > 1)
                                           .ToList();

            // B. 定義 SOP 特徵詞 (僅在 SOP 模式下加分)
            var formKeywords = new[] { "表單", "報告單", "紀錄表", "ISMS-" };
            var frequencyKeywords = new[] { "每年", "定期", "小時", "分鐘", "當下", "立即", "頻率", "時機" };
            var roleKeywords = new[] { "權責", "負責人", "主管", "委員會", "組長" };

            var finalResults = rawResults
                .Select(r =>
                {
                    string content = "";
                    string type = "text";
                    if (r.Payload.TryGetProperty("content", out var c)) content = c.ToString();
                    if (r.Payload.TryGetProperty("type", out var t)) type = t.GetString();

                    // --- 計算加權分數 ---
                    double bonusScore = 0;

                    // 規則 1: 命中使用者關鍵字 (通用規則 - 最重要)
                    foreach (var k in userKeywords)
                    {
                        if (content.Contains(k)) bonusScore += 0.15;
                    }

                    // 規則 2: 情境加權 (只在 SOP 模式下啟動)
                    if (needsMoreContext)
                    {
                        // 表單加權
                        if (formKeywords.Any(k => content.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            bonusScore += 0.25;

                        // 時效/量化數據加權 (包含 4分/5分鐘 等)
                        if (frequencyKeywords.Any(k => content.Contains(k)) || content.Contains("4分"))
                            bonusScore += 0.2;

                        // 權責加權
                        if (roleKeywords.Any(k => content.Contains(k)))
                            bonusScore += 0.1;

                        // 表格類型加權 (表格通常含金量高)
                        if (type == "pdf_table") bonusScore += 0.2;
                    }
                    else
                    {
                        // 一般模式：只給表格一點基本分
                        if (type == "pdf_table") bonusScore += 0.05;
                    }

                    // 規則 3: 文件結尾加分 (抓附錄的絕招)
                    // 如果這頁是第 10 頁之後，稍微加分，防止附錄被擠掉
                    if (needsMoreContext && r.Payload.TryGetProperty("page", out var p) && int.TryParse(p.ToString(), out int pageNum))
                    {
                        if (pageNum > 10) bonusScore += 0.05;
                    }

                    return new { Result = r, FinalScore = r.Score + bonusScore, Content = content };
                })
                .OrderByDescending(x => x.FinalScore) // 依據新分數重新排序
                .Take(finalSendCount)                 // 只取前幾名送出
                .Select(x => x.Result)
                .ToList();

            Console.WriteLine($"📊 重排序後保留: {finalResults.Count} 個精華片段");

            // Debug: 看看誰被選中了 (可選)
            foreach (var r in finalResults)
            {
                string f = r.Payload.TryGetProperty("file", out var p) ? p.GetString() : "?";
                string pg = r.Payload.TryGetProperty("page", out var pgProp) ? pgProp.ToString() : "?";
                //Console.WriteLine($"   + {f} (P.{pg})"); // 想看 log 可以解開
            }

            // ============================================================
            // 🧠 4. 組裝 Prompt 並呼叫 LLM
            // ============================================================
            var context = BuildContext(finalResults);
            var systemPrompt = BuildPrompt(refinedQuery, context);

            // ⚠️ 新增：開始呼叫 Log
            Console.WriteLine($"⏳ [Time: {DateTime.Now:HH:mm:ss}] 正在發送請求給 Groq API... (請耐心等待)");

            // 簡易重試邏輯：如果第一次失敗或空白，自動再試一次
            string answer = await CallGroqWithHistoryAsync(systemPrompt, history);
            // ⚠️ 新增：結束呼叫 Log
            Console.WriteLine($"✅ [Time: {DateTime.Now:HH:mm:ss}] Groq API 回應成功！長度: {answer.Length}");

            if (answer.Contains("錯誤") || answer.Contains("逾時") || answer.Contains("空"))
            {
                Console.WriteLine("🔄 偵測到回答異常，正在自動重試 (Retry)...");
                await Task.Delay(2000); // 等 2 秒
                answer = await CallGroqWithHistoryAsync(systemPrompt, history);
            }

            // ============================================================
            // 📦 5. 打包結果 (這一步是新增的！)
            // ============================================================
            var response = new RagResponse
            {
                Answer = answer,
                Sources = finalResults.Select(r =>
                {
                    // 安全地讀取 Payload
                    string f = r.Payload.TryGetProperty("file", out var propF) ? propF.GetString() ?? "unknown" : "unknown";
                    string p = r.Payload.TryGetProperty("page", out var propP) ? propP.ToString() : "?";
                    string c = r.Payload.TryGetProperty("content", out var propC) ? propC.GetString() ?? "" : "";

                    return new RagSourceDoc
                    {
                        FileName = f,
                        Page = p,
                        Content = c, // ✅ 這裡就是你要顯示在側邊欄的原始文字
                        Score = r.Score // (這裡存的是原始分數，若要存加權後的分數需改寫上面的 Select 邏輯，但通常原始分數夠用了)
                    };
                }).ToList()
            };

            return response;
        }

        // 在 RagQueryService.cs 中

        private string BuildContext(List<QdrantSearchResult> results)
        {
            var sb = new StringBuilder();

            if (results == null || !results.Any())
            {
                return "無相關文件內容。";
            }

            int index = 1;
            foreach (var r in results.OrderByDescending(x => x.Score))
            {
                // ✅ 1. 提取檔名 (重要修改)
                string fileName = "未知來源";
                if (r.Payload.ValueKind == JsonValueKind.Object &&
                    r.Payload.TryGetProperty("file", out var fileElement)) // 讀取你在 IndexService 存的 "file"
                {
                    fileName = fileElement.GetString() ?? "未知來源";
                }

                // ✅ 2. 提取類型 (為表格問題鋪路)
                string type = "text";
                if (r.Payload.ValueKind == JsonValueKind.Object &&
                    r.Payload.TryGetProperty("type", out var typeElement))
                {
                    type = typeElement.GetString() ?? "text";
                }

                // 3. 提取內容
                if (r.Payload.ValueKind == JsonValueKind.Object &&
                    r.Payload.TryGetProperty("content", out var contentElement))
                {
                    var content = contentElement.ValueKind == JsonValueKind.String
                        ? contentElement.GetString()
                        : contentElement.ToString();

                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        // ✅ 4. 重新組裝格式：讓 LLM 清楚看到這是哪份檔案
                        sb.AppendLine($"=== 參考片段 #{index} (來源: {fileName} | 類型: {type}) ===");
                        sb.AppendLine(content);
                        sb.AppendLine(); // 空行分隔
                        index++;
                    }
                }
            }

            return sb.ToString().Trim();
        }

        private string BuildPrompt(string query, string context)
        {
            // ✅ 修改提示詞，教導 LLM 如何閱讀我們的新格式
            return $@"
# 角色設定
您是一名資深的醫療院所資訊安全管理系統（ISMS）執行顧問與稽核員。
您的任務是協助內部人員「準確、詳細且合規」地執行任務。
您的回答將被視為標準作業程序（SOP）的參考依據，因此必須詳盡且具備操作性。

# 任務指令 (由簡轉繁)
使用者若詢問流程或規範，**請勿僅提供摘要**。您必須：
1. **整合資訊**：將散落在不同頁數的資訊（如定義、通報流程、應變小組職責、時效、公式 + 附錄的評分表、）串聯成一個完整的執行劇本。
2. **豐富細節**：每個步驟不能只有一句話。請具體說明：
   - **誰做 (Who)**：明確指出權責單位或人員（如：資安專責人員、資安長）。
   - **做什麼 (What)**：詳細的動作（如：填寫表單名稱、口頭回報、系統操作）。
   - **何時做 (When)**：明確的時間限制（如：1 小時內、立即、定期）。
   - **依據 (Criteria)**：判斷標準（如：事件等級如何區分）。
3. **極度詳盡 (No Summarization)**：使用者需要的是操作手冊。請列出所有細節，**包含具體的表單編號（如 ISMS-X-XX）、部門名稱、職稱、時效數值**。
   - ❌ 避免：填寫相關表單。
   - ✅ 必須：填寫「ISMS-2-07-01 資通安全事件報告單」。
4. **提及表單**：若流程涉及填寫紀錄，務必列出具體的「表單名稱」或「系統名稱」。
5. **內容優先**：請直接回答問題的核心內容（如具體流程步驟、規範數值、定義），**嚴禁**僅回答「文件第幾章提到了什麼」這種導讀式語句
6. **精確引用**：引用文件時，請精準指出出處並嚴格遵守引用規則。
7. **不知為不知**：僅在完全找不到相關資訊時，才說明『未提供足夠資訊』。若已回答大部分內容，則無需加上此聲明。
8. **語氣要求**：回答語氣需正式、客觀、嚴謹，並使用醫療院所常用用語（應、需、不得、依據、確保）。
9. **公式與計算優先**：若問題涉及「計算」、「評估」或「等級判定」，**必須列出完整的數學公式（如 A x B x C）**，並解釋每個變數的定義與評分範圍（0-4分）。
10.**糾錯機制**：若文件內容有明顯的邏輯衝突（如檔名與內文編號不符），請以內文為準並在備註中提示。
11.**完整性檢查**：
   - 有沒有漏掉任何一個係數？（例如：資產價值）
   - 有沒有列出具體的評分標準？（例如：每季發生一次 = 2分）
   - 有沒有將所有相關的內容統整
   - 請檢查所有引用的表單編號是否完整（例如：不可寫成 ISMS-2，必須寫成 ISMS-2-XX-XX）

# 資料解讀與結構化驗證原則
由於來源文件已轉換為 Markdown 表格結構（使用 `|` 分隔），請嚴格執行以下解讀邏輯：

1. **絕對邊界法則 (Markdown Structure)**：
   - **最高優先級**：請利用 `|` 符號作為欄位的**絕對物理邊界**。
   - 若文字被 `|` 隔開（例如：`| 欄位A內容 | 欄位B內容 |`），代表它們屬於完全不同的定義，**嚴禁**將其視為同一句話或混淆。

2. **標題與內容的邏輯一致性 (Logical Consistency)**：
   - 提取前請反思：「這格內容的『意思』是否符合該欄位標題的定義？」
   - ❌ **自動排除矛盾**：若欄位標題是「可用性/時效」，但格子裡卻出現描述「資料外洩」或「正確性」的文字（可能是排版錯誤），請判定為雜訊，**予以捨棄，不可混用**。

3. **量化數據精準錨定**：
   - 針對評分標準，優先鎖定包含**明確邊界條件**（如：大於、小於、具體時間、次數）的描述，並忽略模糊的形容詞。

4. **表格數據優先法則 (Table over Text)**：
   - 當「內文敘述」與「附錄表格」的數據不一致時，**一律以附錄表格中的詳細數據為準**。

5. **強制分類列舉 (Mandatory Categorization)**：
   - 當評分標準或定義依據**資產類別**（如：軟體類、硬體類、文件類、服務類）而有所不同時，**嚴禁**僅提供一個通用的數值（如「1小時」）。
   - **必須**將所有類別分開列出。
   - ✅ 正確範例：「軟體類為 5 分鐘；硬體類為 12 小時。」
   - ❌ 錯誤範例：「一般而言為 1 小時。」(這會被視為資訊隱匿)

# 引用規則 (非常重要)
1. 我提供給你的資料中，每個片段開頭都有 `(來源: XXX.pdf)`。
2. 當你引用某段內容時，**必須**在該句或該段落的**末尾**標註來源檔名。
   - ❌ 錯誤範例：文件 ISMS-2-01 指出應定期審查權限...
   - ✅ 正確範例：應每半年定期審查使用者權限，並保留紀錄 [來源: ISMS-2-01_權限管理.pdf]。
3. 若有多個來源支持同一論點，請列出所有相關檔名。
4. 如果資料來自表格類文件 (Excel)，請特別標註。

# 重要要求：動態架構生成
請根據『問題性質』自動選擇最合適的回答結構，而 **不要強制套用固定模板**。
若使用者詢問「流程」、「程序」或「如何執行」，請**嚴格依照**以下邏輯進行拆解，缺一不可：
1. **拒絕摘要**：不要寫成一段落，必須拆解成「階段/步驟」。
2. **SOP 矩陣填空**：對於每一個步驟，您必須像稽核員檢查一樣，試圖找出並列出以下四要素（若 Context 中有提及）：
   - 👮 **權責人員 (Who)**：是資安長？還是專責人員？請精確點名。
   - 📝 **具體動作 (What)**：不只是「通報」，而是「口頭通報」還是「填單」？
   - 📄 **表單/系統 (Form)**：**務必列出表單編號** (如 ISMS-2-07-01)。若文中提到「填寫報告單」，請找出該報告單的編號。
   - ⏰ **時效/標準 (Criteria)**：**務必找出數字**！(如：1小時內、每年一次、每月兩次)。
# 範例結構 (請模仿此結構作答)
**階段一：事件發現**
- **權責**：發現人、資安專責人員
- **動作**：通知專責人員，並初步研判是否為資安事件
- **表單**：無
- **時效**：發現當下立即執行

**階段二：通報 (關鍵階段)**
- **權責**：資安長
- **動作**：確認等級後，對外部 H-ISAC 進行通報
- **表單**：ISMS-2-07-01 資通安全事件報告單
- **時效**：**確認後 1 小時內** (這是硬性規定) [來源: ISMS-2-07.pdf]
若問題是「定義」：可自然形成定義、特性、文件引用。
若問題是「要求／規定」：可自然形成要求、依據、執行重點。
若問題是「比較／差異」：可自然形成比較維度、差異點、適用性。
若問題是「範圍／原則」：可自然形成判定基準、引用依據、實施範疇。
若問題是「如何評估/計算」，請採用：
**一、核心公式/準則**
- 公式：... [來源]
- 變數定義：... [來源]

**二、評估步驟詳解**
- 步驟 1：[變數 A] 評分標準 (0-4分定義) ... [來源]
- 步驟 2：[變數 B] 評分標準 ... [來源]

請**完全由模型自行決定最適合的架構**，但必須符合：
- 條理清晰，結構合理
- **內容詳盡 (Details First)**：表格或清單請盡量完整列出，不要省略。
- 可稽核：每段內容都能追溯到文件。

# 提供之文件內容 (Context)
{context}

# 使用者問題
{query}

# 回答要求
- 回答需簡潔但具有專業深度。
- **不要**使用「根據文件內容...」作為開頭，請直接切入重點。
- 若查無內容需明確說明。

請開始依據文件內容作答：
";
        }


        // ✅ 請將此方法加在 RagQueryService 類別內部
        private async Task<string> CallGroqWithHistoryAsync(string systemPrompt, List<ChatMessage> history)
        {
            // 1. 建立訊息列表
            var messages = new List<object>();

            // 2. 加入 System Prompt (包含最新的 RAG Context)
            messages.Add(new { role = "system", content = systemPrompt });

            // 3. 加入歷史紀錄 (最近 6 則，避免 Token 爆炸)
            foreach (var msg in history.TakeLast(6))
            {
                // 確保 role 是 API 接受的格式 (user/assistant)
                messages.Add(new { role = msg.Role, content = msg.Content });
            }

            // 4. 呼叫 API
            var body = new
            {
                model = _groqModel,
                messages = messages,
                temperature = 0.2, // 低溫確保回答精準
                max_tokens = 2048
            };

            var response = await _groqClient.PostAsJsonAsync("chat/completions", body);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return $"❌ Groq API 錯誤：{json}";

            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "(無回應)";
            }
            catch (Exception ex)
            {
                return $"❌ 解析錯誤: {ex.Message} (原始回應: {json})";
            }
        }

        // ============================================
        // 🆕 取得前端需要的 Top 文件來源
        // ============================================
        public async Task<List<RagSource>> GetTopSourcesAsync(string query, int topK = 5)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<RagSource>();

            // 1) 文字轉 embedding（Voyage）
            var embedding = await _embedder.EmbedAsync(query);

            // 2) 搜尋向量資料庫（Qdrant）
            var results = await _indexer.SearchAsync(embedding, topK);

            // 3) 整理成前端可用格式
            return results.Select(r =>
            {
                // Payload = JsonElement
                string fileName =
                    r.Payload.TryGetProperty("fileName", out var f)
                        ? f.GetString() ?? ""
                        : "unknown";

                string preview =
                    r.Payload.TryGetProperty("content", out var p)
                        ? p.GetString()?.Substring(0, Math.Min(120, p.GetString()!.Length)) + "..."
                        : "(無內容)";

                return new RagSource
                {
                    FileName = fileName,
                    PreviewText = preview,
                    Score = r.Score
                };
            }).ToList();
        }
    }

    // ============================================
    // 🆕 給前端的模型
    // ============================================
    public class RagSource
    {
        public string FileName { get; set; } = "";
        public string PreviewText { get; set; } = "";
        public double Score { get; set; }
    }

}
