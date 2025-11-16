using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace RagPipeline.VectorDb
{
    public class QdrantIndexer
    {
        private readonly HttpClient _http;
        private readonly string _endpoint;
        private const string CollectionName = "security_docs_rag";
        private const int VectorSize = 1024;

        public QdrantIndexer(string endpoint = "http://localhost:6333")
        {
            _endpoint = endpoint.TrimEnd('/');
            _http = new HttpClient { BaseAddress = new Uri(_endpoint) };
        }

        // =====================================================
        // ✅ 確保 collection 存在（否則建立）
        // =====================================================
        public async Task EnsureCollectionAsync()
        {
            var check = await _http.GetAsync($"/collections/{CollectionName}");

            if (check.IsSuccessStatusCode)
            {
                Console.WriteLine($"✅ Qdrant collection exists: {CollectionName}");
                return;
            }

            Console.WriteLine($"⚠ Collection not found. Creating: {CollectionName}");

            var payload = new
            {
                vectors = new
                {
                    size = VectorSize,
                    distance = "Cosine"
                }
            };

            var res = await _http.PutAsJsonAsync($"/collections/{CollectionName}", payload);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Collection create failed: {error}");
            }

            Console.WriteLine($"✅ Qdrant collection created: {CollectionName}");
        }

        // =====================================================
        // ✅ 刪除整個 collection（index 前詢問用）
        // =====================================================
        public async Task DeleteCollectionAsync()
        {
            var res = await _http.DeleteAsync($"/collections/{CollectionName}");

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Failed to delete collection: {error}");
            }

            Console.WriteLine($"🗑 已刪除 collection：{CollectionName}");
        }

        // =====================================================
        // ✅ 重新建立 collection
        // =====================================================
        public async Task RecreateCollectionAsync()
        {
            await DeleteCollectionAsync();

            var payload = new
            {
                vectors = new
                {
                    size = VectorSize,
                    distance = "Cosine"
                }
            };

            var res = await _http.PutAsJsonAsync($"/collections/{CollectionName}", payload);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Collection recreate failed: {error}");
            }

            Console.WriteLine($"✅ Collection recreated: {CollectionName}");
        }

        // =====================================================
        // ✅ Upsert vector
        // =====================================================
        public async Task UpsertAsync(
            string id,
            float[] vector,
            Dictionary<string, object> payload)
        {
            var body = new
            {
                points = new[]
                {
                    new
                    {
                        id = id,
                        vector = vector,
                        payload = payload
                    }
                }
            };

            var res = await _http.PutAsJsonAsync($"/collections/{CollectionName}/points", body);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Qdrant upsert failed: {error}");
            }

            Console.WriteLine($"✅ Upsert OK: {id}");
        }

        // =====================================================
        // ✅ Search top-K
        // =====================================================
        public async Task<List<QdrantSearchResult>> SearchAsync(float[] queryVector, int topK = 5)
        {
            var body = new
            {
                vector = queryVector,
                limit = topK,
                with_payload = true  // 🆕 明確要求返回 payload
            };

            var res = await _http.PostAsJsonAsync($"/collections/{CollectionName}/points/search", body);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Qdrant search failed: {error}");
            }

            var json = await res.Content.ReadAsStringAsync();

            // 🆕 添加調試日誌
            Console.WriteLine($"🔍 Qdrant 搜索回應 JSON 長度: {json.Length}");
            if (json.Length < 500)
            {
                Console.WriteLine($"🔍 Qdrant 回應內容: {json}");
            }

            var result = JsonSerializer.Deserialize<QdrantSearchResponse>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    // 🆕 添加更寬鬆的解析設定
                    NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                });

            // 🆕 檢查反序列化結果
            if (result?.Result == null)
            {
                Console.WriteLine("❌ 反序列化結果為空");
                return new List<QdrantSearchResult>();
            }

            Console.WriteLine($"✅ 成功反序列化 {result.Result.Count} 個結果");
            return result.Result;
        }

        // =====================================================
        // ✅ Delete by ID
        // =====================================================
        public async Task DeleteAsync(string id)
        {
            var body = new
            {
                points = new[] { id }
            };

            var res = await _http.PostAsJsonAsync($"/collections/{CollectionName}/points/delete", body);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Qdrant delete failed: {error}");
            }

            Console.WriteLine($"✅ Deleted: {id}");
        }

        // =====================================================
        // 🆕 Delete all vectors where payload.file = fileName
        // =====================================================
        public async Task DeleteByFileNameAsync(string fileName)
        {
            var body = new
            {
                filter = new
                {
                    must = new object[]
                    {
                new
                {
                    key = "file",
                    match = new { value = fileName }
                }
                    }
                }
            };

            var res = await _http.PostAsJsonAsync(
                $"/collections/{CollectionName}/points/delete",
                body);

            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync();
                throw new Exception($"❌ Qdrant delete by file failed: {error}");
            }

            Console.WriteLine($"🗑 已刪除文件 {fileName} 的所有向量資料");
        }

    }



    // =====================================================
    // ✅ Models（移除 TimeSpan）
    // =====================================================
    public class QdrantSearchResponse
    {
        public List<QdrantSearchResult> Result { get; set; } = new List<QdrantSearchResult>();
        public string Status { get; set; } = string.Empty;
        public double Time { get; set; }
    }

    public class QdrantSearchResult
    {
        public string Id { get; set; } = string.Empty;
        public float Score { get; set; }
        public int Version { get; set; }
        // 🆕 使用 JsonElement 來正確處理動態 Payload
        public JsonElement Payload { get; set; }
    }
}
