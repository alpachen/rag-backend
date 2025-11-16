# api/app.py
import os
from fastapi import FastAPI
from pydantic import BaseModel
from dotenv import load_dotenv
from groq import Groq

load_dotenv()
api_key = os.getenv("GROQ_API_KEY")
client = Groq(api_key=api_key)

app = FastAPI()

class QueryRequest(BaseModel):
    prompt: str

class QueryResponse(BaseModel):
    response: str

@app.get("/health")
def health():
    return {"ok": True}

@app.post("/chat", response_model=QueryResponse)
def chat(req: QueryRequest):
    # 系統提示：條列式＋拒答策略（你之前的需求）
    system_prompt = (
        "你是專業的資安文件顧問。必須：\n"
        "1) 優先根據提供的檢索片段作答；\n"
        "2) 無依據時明確拒答並說明需要的資訊；\n"
        "3) 以繁體中文輸出；\n"
        "4) 以條列式呈現關鍵重點；\n"
        "5) 嚴禁瞎掰來源。"
    )
    messages = [
        {"role": "system", "content": system_prompt},
        {"role": "user",    "content": req.prompt}
    ]
    # 模型：你選 A → llama3-70b-8192
    chat = client.chat.completions.create(
        model="llama3-70b-8192",
        messages=messages,
        temperature=0.3,
        top_p=0.9,
        max_tokens=600
    )
    text = chat.choices[0].message.content.strip()
    return QueryResponse(response=text)
