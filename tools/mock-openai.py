"""本地假 OpenAI 兼容端点（SSE 流式，和 pi-ai 的真实请求格式一致）。

用法：python3 tools/mock-openai.py [port]
模型名约定：
  ok-*              正常返回一段文本
  empty-length-*    返回空内容 + finish_reason=length（模拟被长度截断）
  empty-reasoning-* 返回空 content + reasoning_content（模拟推理模型把输出用在思考上）
请求记录追加到 tools/mock-openai.hits.log。
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

HITS = "tools/mock-openai.hits.log"


def build_response(model: str):
    """返回 (content, reasoning_content, finish_reason)。"""
    if model.startswith("empty-length"):
        return "", None, "length"
    if model.startswith("empty-reasoning"):
        return "", "想了很多但没输出", "stop"
    return json.dumps({"echo": True, "model": model}), None, "stop"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode()
        with open(HITS, "a", encoding="utf-8") as f:
            f.write("PATH=%s\nBODY=%s\n---\n" % (self.path, body[:500]))

        req = json.loads(body)
        model = req.get("model", "")
        content, reasoning, finish = build_response(model)

        if req.get("stream"):
            # pi-ai 用的是流式：必须回 SSE，否则它会把响应判成解析错误
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()

            def emit(delta, fr=None):
                payload = {"choices": [{"index": 0, "delta": delta}]}
                if fr:
                    payload["choices"][0]["finish_reason"] = fr
                self.wfile.write(("data: " + json.dumps(payload) + "\n\n").encode())
                self.wfile.flush()

            if reasoning:
                emit({"reasoning_content": reasoning})
            if content:
                emit({"content": content})
            emit({}, finish)
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
            return

        out = {
            "choices": [{
                "finish_reason": finish,
                "message": {
                    "role": "assistant",
                    "content": content,
                    **({"reasoning_content": reasoning} if reasoning else {}),
                },
            }],
            "usage": {"prompt_tokens": 5, "completion_tokens": 3},
        }
        data = json.dumps(out).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *a):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 18899
    print("mock openai on %d" % port, flush=True)
    HTTPServer(("127.0.0.1", port), Handler).serve_forever()
