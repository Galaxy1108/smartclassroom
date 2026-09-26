"""本地假 OpenAI 兼容端点：验证 pi-ai 边车真的发出请求并取回内容。

用法：python3 tools/mock-openai.py [port]
请求会追加记录到 tools/mock-openai.hits.log。
"""
import json
import sys
from http.server import BaseHTTPRequestHandler, HTTPServer

HITS = "tools/mock-openai.hits.log"


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(n).decode()
        with open(HITS, "a", encoding="utf-8") as f:
            f.write("PATH=%s\nBODY=%s\n---\n" % (self.path, body[:500]))

        req = json.loads(body)
        content = json.dumps({"echo": True, "model": req.get("model")})
        if req.get("stream"):
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            for chunk in (
                'data: {"choices":[{"delta":{"content":%s}}]}\n\n' % json.dumps(content),
                "data: [DONE]\n\n",
            ):
                self.wfile.write(chunk.encode())
                self.wfile.flush()
        else:
            out = {
                "choices": [{"message": {"role": "assistant", "content": content}}],
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
