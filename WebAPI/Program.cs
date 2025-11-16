using Microsoft.AspNetCore.Mvc;
using RagPipeline.Services;
using RagPipeline.Embeddings;
using RagPipeline.VectorDb;
using RagPipeline.Extractors;
using RagPipeline.Processing;
using RagDataPipeline.Services;
using WebAPI.Models;

var builder = WebApplication.CreateBuilder(args);

// ============================
// CORS（前端才能呼叫 API）
// ============================
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", p =>
        p.AllowAnyOrigin()
         .AllowAnyHeader()
         .AllowAnyMethod());
});

// ============================
// 設定：加入 Admin Key
// ============================
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// ============================
// Dependency Injection
// ============================
builder.Services.AddControllers();

builder.Services.AddSingleton<VoyageEmbedder>();
builder.Services.AddSingleton<QdrantIndexer>();
builder.Services.AddSingleton<PdfTextExtractor>();
builder.Services.AddSingleton<ExcelTextExtractor>();
builder.Services.AddSingleton<TextChunker>();
builder.Services.AddSingleton<DocumentIndexService>();
builder.Services.AddSingleton<RagQueryService>();

var app = builder.Build();

app.UseCors("AllowAll");

// ============================
// Middleware
// ============================
app.UseHttpsRedirection();
app.UseAuthorization();

// ============================
// Admin 驗證方法
// ============================
bool IsAdmin(HttpRequest req)
{
    var adminKey = builder.Configuration["Admin:ApiKey"];
    if (!req.Headers.TryGetValue("x-admin-key", out var key))
        return false;

    return key == adminKey;
}

// ============================
// 一般使用者：RAG 查詢（已加入 sources）
// ============================
app.MapPost("/api/rag/query", async (
    [FromBody] RagQueryRequest request,
    RagQueryService ragService) =>
{
    try
    {
        // AI 回答
        var answer = await ragService.AskAsync(request.Question);

        // 文件相似度
        var docs = await ragService.GetTopSourcesAsync(request.Question);

        var response = new
        {
            answer,
            sources = docs.Select(d => new
            {
                title = d.FileName,
                snippet = d.PreviewText,
                score = d.Score,
                // url = $"https://rag-backend-5jm5.onrender.com/storage/{d.FileName}"
            })
        };

        return Results.Ok(response);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ============================
// 管理者：列出所有文件
// ============================
app.MapGet("/api/admin/files", (HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();

    string folder = Path.Combine("Data", "PDFs");
    Directory.CreateDirectory(folder);

    var files = Directory.GetFiles(folder)
                         .Select(Path.GetFileName)
                         .ToList();

    return Results.Ok(files);
});

// ============================
// 管理者：上傳文件 + 建立索引
// ============================
app.MapPost("/api/admin/upload", async (
    HttpRequest req,
    DocumentIndexService indexService) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!req.Form.Files.Any()) return Results.BadRequest("No file uploaded.");

    var file = req.Form.Files[0];
    string folder = Path.Combine("Data", "PDFs");
    Directory.CreateDirectory(folder);

    string filePath = Path.Combine(folder, file.FileName);

    using (var fs = File.Create(filePath))
        await file.CopyToAsync(fs);

    await indexService.IndexSingleFileAsync(filePath);

    return Results.Ok(new { message = "File uploaded & indexed." });
});

// ============================
// 管理者：刪除文件 + 移除 Qdrant 向量
// ============================
app.MapDelete("/api/admin/delete/{filename}", async (
    string filename,
    HttpRequest req,
    DocumentIndexService indexService) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();

    await indexService.DeleteFileAsync(filename);

    return Results.Ok(new { message = $"Deleted {filename}" });
});

// ============================
// 管理者：重建全部索引
// ============================
app.MapPost("/api/admin/reindex", async (
    HttpRequest req,
    DocumentIndexService indexService) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();

    await indexService.RebuildAllAsync();

    return Results.Ok(new { message = "Full reindex completed." });
});

// ============================
// 管理者：重建單一文件
// ============================
app.MapPost("/api/admin/reindex-one/{filename}", async (
    string filename,
    HttpRequest req,
    DocumentIndexService indexService) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();

    await indexService.ReindexSingleFileAsync(filename);

    return Results.Ok(new { message = $"Reindexed {filename}" });
});

// ============================
// 🔥 Health Check（給 Render / UptimeRobot 用）
// ============================
app.MapGet("/", () => Results.Ok(new { status = "alive" }));

// ============================
app.MapControllers();
app.Run();