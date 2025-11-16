using RagPipeline.Services;
using RagPipeline.Embeddings;   // ✅ VoyageEmbedder
using RagPipeline.VectorDb;
using RagPipeline.Extractors;
using RagPipeline.Processing;
using System.Text.Json;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("使用方式:");
            Console.WriteLine("  index <folderPath>   建立索引");
            Console.WriteLine("  chat                 啟動對話模式");
            return;
        }

        // ------------------------------------------------------
        // ✅ 使用 Voyage Embedding（最推薦、支援中文）
        // ------------------------------------------------------
        var embedder = new VoyageEmbedder();

        // ✅ 建立 Qdrant Indexer
        var indexer = new QdrantIndexer();

        // ✅ File extractors
        var pdf = new PdfTextExtractor();
        var excel = new ExcelTextExtractor();
        var chunker = new TextChunker();

        // ✅ RAG Query（Voyage embedding + Groq LLM）
        var rag = new RagQueryService(embedder, indexer);

        // ------------------------------------------------------
        // ✅ index 模式
        // ------------------------------------------------------
        if (args[0] == "index")
        {
            if (args.Length < 2)
            {
                Console.WriteLine("請指定資料夾路徑：");
                return;
            }

            var folder = args[1];
            if (!Directory.Exists(folder))
            {
                Console.WriteLine("資料夾不存在.");
                return;
            }

            Console.WriteLine("⚠ 是否要清空整個 Qdrant collection？ (y/N)");
            var input = Console.ReadLine()?.Trim().ToLower();

            if (input == "y")
            {
                Console.WriteLine("🗑 正在清空 collection...");
                await indexer.RecreateCollectionAsync();
            }
            else
            {
                Console.WriteLine("✅ 保留現有 collection，不清空");
                await indexer.EnsureCollectionAsync();
            }

            await RunIndexing(folder, pdf, excel, chunker, embedder, indexer);
            return;
        }

        // ------------------------------------------------------
        // ✅ chat 模式（RAG）
        // ------------------------------------------------------
        if (args[0] == "chat")
        {
            // 🆕 先執行嵌入測試
            Console.WriteLine("🔍 執行嵌入相似度測試...");
            await TestEmbeddingSimilarity(embedder, indexer);
            Console.WriteLine("\n" + new string('=', 50));
            Console.WriteLine("開始聊天模式...");
            await RunChat(rag);
            return;
        }
    }

    // =====================================================================
    // ✅ index：PDF/Excel → chunk → Voyage Embedding → Qdrant upsert
    // =====================================================================
    static async Task RunIndexing(
        string folder,
        PdfTextExtractor pdf,
        ExcelTextExtractor excel,
        TextChunker chunker,
        VoyageEmbedder embedder,
        QdrantIndexer indexer)
    {
        Console.WriteLine($"🔍 開始建立索引：{folder}");

        var files = Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"找到 {files.Count} 個文件.");

        foreach (var file in files)
        {
            Console.WriteLine($"📄 處理：{Path.GetFileName(file)}");

            string text = file.EndsWith(".pdf")
                ? pdf.ExtractText(file)
                : excel.ExtractText(file);

            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("⚠ 無文字可索引，跳過。");
                continue;
            }

            var chunks = chunker.Chunk(text);
            int order = 0;

            foreach (var c in chunks)
            {
                // ✅ 使用 Voyage Embedding
                var vec = await embedder.EmbedAsync(c);

                var pointId = Guid.NewGuid().ToString();

                var payload = new Dictionary<string, object>
                {
                    ["file"] = Path.GetFileName(file),
                    ["order"] = order,
                    ["content"] = c
                };

                await indexer.UpsertAsync(pointId, vec, payload);
                order++;
            }

            Console.WriteLine($"✅ 完成：{Path.GetFileName(file)}");
        }

        Console.WriteLine("✅ 全部索引完成！");
    }

    // =====================================================================
    // ✅ chat：使用者輸入 → RAG → Llama 70B（Groq）回答
    // =====================================================================
    static async Task RunChat(RagQueryService rag)
    {

        Console.WriteLine("✅ RAG Chat 已啟動（輸入 exit 離開）");

        while (true)
        {
            Console.Write("\n你：");
            var q = Console.ReadLine();

            if (q == null || q.Trim().ToLower() == "exit")
                break;

            var answer = await rag.AskAsync(q);
            Console.WriteLine($"\n--- 回答 ---\n{answer}\n");
        }

        Console.WriteLine("👋 已離開聊天模式。");
    }

    // 在 Program.cs 中添加
    public static async Task TestEmbeddingSimilarity(VoyageEmbedder embedder, QdrantIndexer indexer)
    {
        var testQueries = new[]
        {
        "如何界定ISMS的範圍",
        "資訊資產分類標準",
        "風險評鑑程序",
        "內部稽核查核"
    };

        foreach (var query in testQueries)
        {
            Console.WriteLine($"\n🧪 測試查詢: {query}");

            try
            {
                var vector = await embedder.EmbedAsync(query);
                var results = await indexer.SearchAsync(vector, 3);

                if (results.Any())
                {
                    Console.WriteLine($"✅ 檢索到 {results.Count} 個相關結果:");
                    foreach (var result in results)
                    {
                        Console.WriteLine($"   📊 相似度: {result.Score:F3}");
                        // 🆕 詳細檢查所有 Payload 字段
                        // 🆕 正確讀取 JsonElement Payload
                        Console.WriteLine($"   🔍 Payload 字段:");
                        if (result.Payload.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in result.Payload.EnumerateObject())
                            {
                                var value = property.Value.ValueKind == JsonValueKind.String
                                    ? property.Value.GetString()
                                    : property.Value.ToString();

                                var valuePreview = value?.Length > 30
                                    ? value.Substring(0, 30) + "..."
                                    : value ?? "null";
                                Console.WriteLine($"      {property.Name}: {valuePreview}");
                            }
                        }
                        else
                        {
                            Console.WriteLine($"      Payload 類型: {result.Payload.ValueKind}");
                        }

                        Console.WriteLine(); // 空行分隔
                    }
                }
                else
                {
                    Console.WriteLine("❌ 沒有檢索到相關結果");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 測試失敗: {ex.Message}");
            }
        }
    }
}
