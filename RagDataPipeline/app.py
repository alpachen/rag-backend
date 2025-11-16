import os, glob, math, uuid
from typing import List, Dict, Optional
from dataclasses import dataclass

from fastapi import FastAPI
from pydantic import BaseModel
from dotenv import load_dotenv

import numpy as np
from fastembed import TextEmbedding  # 會嘗試多個模型
import onnxruntime as ort

from qdrant_client import QdrantClient
from qdrant_client.http.models import Distance, VectorParams, PointStruct, Filter, FieldCondition, MatchValue

from groq import Groq
import uvicorn

# -------------------- 基本設定 --------------------
load_dotenv()
GROQ_API_KEY = os.getenv("GROQ_API_KEY")
assert GROQ_API_KEY, "請在 .env 設定 GROQ_API_KEY"
GROQ_MODEL = "gemma2-9b-it"

DOCS_DIR = "./docs"
COLLECTION = "rag_docs"

QDRANT_URL = os.getenv("QDRANT_URL", "http://localhost:6333")
QDRANT_API_KEY = os.getenv("QDRANT_API_KEY", None)

CHUNK_SIZE = 900
CHUNK_OVERLAP = 150
TOP_K = 10
MIN_SCORE = 0.70

# -------------------- Chunker --------------------
def split_with_overlap(text: str, size: int, overlap: int) -> List[str]:
    chunks, i = [], 0
    n = len(text)
    step = size - overlap
    if step <= 0: step = size
    while i < n:
        chunks.append(text[i:i+size])
        i += step
    return chunks

# -------------------- Embedder（m3e-base 優先，其次 multilingual 快模） --------------------
class Embedder:
    def __init__(self):
        self.model_name = None
        self._embedder = None
        self.dim = None

        tried = []
        for name in [
            "moka-ai/m3e-base",                 # 與你原規劃一致（若 fastembed 支援會成功）
            "intfloat/multilingual-e5-small",   # 快且穩（中文也可）
        ]:
            try:
                self._embedder = TextEmbedding(model_name=name, cache_dir="./emb_cache")
                vec = self._embedder.embed(["test"])[0]
                self.dim = len(vec)
                self.model_name = name
                print(f"[Embedder] 使用模型：{name} (dim={self.dim})")
                break
            except Exception as e:
                tried.append((name, str(e)))
                continue

        if self._embedder is None:
            raise RuntimeError(f"無法載入任何嵌入模型，嘗試：{tried}")

    def encode(self, texts: List[str]) -> np.ndarray:
        vecs = list(self._embedder.embed(texts, batch_size=64, normalize=True))
        return np.asarray(vecs, dtype=np.float32)

# -------------------- Qdrant --------------------
def init_qdrant(client: QdrantClient, collection: str, dim: int):
    exists = False
    try:
        info = client.get_collection(collection)
        exists = True
        have_dim = info.vectors_count > 0
        print(f"[Qdrant] 已存在集合：{collection}, vectors_count={info.vectors_count}, 假設維度=dim({dim})")
    except Exception:
        pass

    if not exists:
        client.recreate_collection(
            collection_name=collection,
            vectors_config=VectorParams(size=dim, distance=Distance.COSINE),
        )
        print(f"[Qdrant] 建立集合：{collection} (dim={dim}, cosine)")

# -------------------- Groq --------------------
groq_client = Groq(api_key=GROQ_API_KEY)

SYSTEM_RULES = """你是一個專注特定領域的助理。請：
- 以條列式作答（短句、清楚）。
- 僅使用檢索到的內容作答；若檢索不足，請明確說「資料不足，無法可靠回答」，並提出可補充的文件類型。
- 嚴格拒答與領域無關的問題，回覆：「超出範圍」。"""

def call_groq(prompt: str) -> str:
    # chat.completions（流式可再升級）
    resp = groq_client.chat.completions.create(
        model=GROQ_MODEL,
        messages=[
            {"role":"system","content": SYSTEM_RULES},
            {"role":"user","content": prompt}
        ],
        temperature=0.3,
        max_tokens=512,
        top_p=0.9
    )
    return resp.choices[0].message.content.strip()

# -------------------- FastAPI --------------------
app = FastAPI(title="RAG x Groq 超速原型")

@dataclass
class DocChunk:
    id: str
    text: str
    source: str

class IndexRequest(BaseModel):
    # 可指定目錄；預設 ./docs
    directory: Optional[str] = None

class QueryRequest(BaseModel):
    query: str

embedder = None
qdrant = None

@app.on_event("startup")
def _startup():
    global embedder, qdrant
    embedder = Embedder()
    qdrant = QdrantClient(url=QDRANT_URL, api_key=QDRANT_API_KEY)
    init_qdrant(qdrant, COLLECTION, embedder.dim)

@app.post("/reindex")
def reindex(req: IndexRequest):
    directory = req.directory or DOCS_DIR
    files = sorted(glob.glob(os.path.join(directory, "*.txt")))
    if not files:
        return {"ok": False, "msg": f"找不到 .txt 檔案於 {directory}"}

    points = []
    for f in files:
        with open(f, "r", encoding="utf-8", errors="ignore") as rf:
            raw = rf.read().strip()
        chunks = split_with_overlap(raw, CHUNK_SIZE, CHUNK_OVERLAP)
        vecs = embedder.encode(chunks)
        for i, (ch, v) in enumerate(zip(chunks, vecs)):
            pid = str(uuid.uuid4())
            points.append(
                PointStruct(
                    id=pid,
                    vector=v.tolist(),  # 已 normalize=True
                    payload={
                        "text": ch,
                        "source": os.path.basename(f),
                        "chunk_idx": i
                    }
                )
            )

    if points:
        qdrant.upsert(collection_name=COLLECTION, points=points)
    return {"ok": True, "files": files, "points": len(points)}

@app.post("/query")
def query(req: QueryRequest):
    vec = embedder.encode([req.query])[0].tolist()
    sr = qdrant.search(
        collection_name=COLLECTION,
        query_vector=vec,
        limit=TOP_K,
        score_threshold=MIN_SCORE,
        with_payload=True
    )

    hits = [(r.score, r.payload["text"], r.payload.get("source","?")) for r in sr]
    context = "\n\n".join([f"- ({s:.3f}) [{src}] {txt}" for s, txt, src in hits])

    prompt = f"""[檢索到的相關段落]
{context if context else "(無結果或分數不足)"} 

[使用者問題]
{req.query}

[要求]
請以條列方式回答；若檢索內容不足請回覆「資料不足，無法可靠回答」，並列出你希望我補充的檔案或關鍵字。"""

    answer = call_groq(prompt)
    return {"answer": answer, "hits": len(hits), "minScore": MIN_SCORE, "topK": TOP_K}

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8000)
