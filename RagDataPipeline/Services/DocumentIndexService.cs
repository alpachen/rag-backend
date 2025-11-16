using RagPipeline.Embeddings;
using RagPipeline.VectorDb;
using RagPipeline.Extractors;
using RagPipeline.Processing;

namespace RagDataPipeline.Services
{
    public class DocumentIndexService
    {
        private readonly PdfTextExtractor _pdf;
        private readonly ExcelTextExtractor _excel;
        private readonly TextChunker _chunker;
        private readonly VoyageEmbedder _embedder;
        private readonly QdrantIndexer _qdrant;

        private readonly string _docFolder =
            Path.Combine("Data", "Docs");  // 所有上傳文件都放這裡

        public DocumentIndexService(
            PdfTextExtractor pdf,
            ExcelTextExtractor excel,
            TextChunker chunker,
            VoyageEmbedder embedder,
            QdrantIndexer qdrant)
        {
            _pdf = pdf;
            _excel = excel;
            _chunker = chunker;
            _embedder = embedder;
            _qdrant = qdrant;

            if (!Directory.Exists(_docFolder))
                Directory.CreateDirectory(_docFolder);
        }

        // ======================================================================
        // 🚀 1. 上傳單一文件 → 切片 → 嵌入 → Qdrant upsert
        // ======================================================================
        public async Task IndexSingleFileAsync(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            Console.WriteLine($"📄 [IndexSingle] 處理：{fileName}");

            string text = ExtractFileText(filePath);
            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine("⚠ 無文字可索引，跳過。");
                return;
            }

            var chunks = _chunker.Chunk(text);

            int order = 0;
            foreach (var chunk in chunks)
            {
                var vec = await _embedder.EmbedAsync(chunk);
                var id = Guid.NewGuid().ToString();

                var payload = new Dictionary<string, object>
                {
                    ["file"] = fileName,
                    ["order"] = order,
                    ["content"] = chunk
                };

                await _qdrant.UpsertAsync(id, vec, payload);
                order++;
            }

            Console.WriteLine($"✅ [IndexSingle] 完成：{fileName}");
        }

        // ======================================================================
        // 🚀 2. 重建單一文件（刪除舊向量 → 重新建立）
        // ======================================================================
        public async Task ReindexSingleFileAsync(string fileName)
        {
            Console.WriteLine($"♻ [ReindexOne] 重新索引：{fileName}");

            string path = Path.Combine(_docFolder, fileName);

            if (!File.Exists(path))
                throw new FileNotFoundException($"找不到文件：{path}");

            // 1) 刪除舊向量
            await _qdrant.DeleteByFileNameAsync(fileName);

            // 2) 重建新向量
            await IndexSingleFileAsync(path);
        }

        // ======================================================================
        // 🚀 3. 重建全部文件
        // ======================================================================
        public async Task RebuildAllAsync()
        {
            Console.WriteLine("♻ [RebuildAll] 清空 Qdrant collection...");
            await _qdrant.RecreateCollectionAsync();

            var files = Directory.GetFiles(_docFolder)
                                 .Where(f => f.EndsWith(".pdf") ||
                                             f.EndsWith(".xlsx") ||
                                             f.EndsWith(".xls"))
                                 .ToList();

            Console.WriteLine($"📌 找到 {files.Count} 個文件");

            foreach (var file in files)
                await IndexSingleFileAsync(file);

            Console.WriteLine("🎉 [RebuildAll] 全部重新索引完成！");
        }

        // ======================================================================
        // 🚀 4. 刪除文件 + 刪除對應向量
        // ======================================================================
        public async Task DeleteFileAsync(string fileName)
        {
            string path = Path.Combine(_docFolder, fileName);

            Console.WriteLine($"🗑 [DeleteFile] 刪除：{fileName}");

            if (File.Exists(path))
                File.Delete(path);

            await _qdrant.DeleteByFileNameAsync(fileName);

            Console.WriteLine("🧹 [DeleteFile] 文件與向量已清理。");
        }

        // ======================================================================
        // 工具函式：依副檔名抽取文字
        // ======================================================================
        private string ExtractFileText(string filePath)
        {
            if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                return _pdf.ExtractText(filePath);

            if (filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".xls", StringComparison.OrdinalIgnoreCase))
                return _excel.ExtractText(filePath);

            throw new InvalidOperationException("Unsupported file type: " + filePath);
        }
    }
}

