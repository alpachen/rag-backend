using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RagPipeline.Embeddings
{
    public class VoyageEmbedder
    {
        private readonly HttpClient _client;
        private readonly string _apiKey;
        private const string ModelName = "voyage-multilingual-2";

        public VoyageEmbedder()
        {
            _apiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
                     ?? throw new Exception("環境變數 VOYAGE_API_KEY 未設定");

            _client = new HttpClient
            {
                BaseAddress = new Uri("https://api.voyageai.com"),
                Timeout = TimeSpan.FromSeconds(60)
            };

            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        public async Task<float[]> EmbedAsync(string text)
        {
            var body = new
            {
                model = ModelName,
                input = text
            };

            var response = await _client.PostAsJsonAsync("/v1/embeddings", body);

            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Voyage API error: {json}");

            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement
                .GetProperty("data")[0]
                .GetProperty("embedding")
                .EnumerateArray()
                .Select(x => x.GetSingle())
                .ToArray();

            return arr;
        }
    }
}

