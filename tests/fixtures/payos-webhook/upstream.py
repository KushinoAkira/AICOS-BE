"""Isolated transport fixture. No provider, application, credentials or database."""
import hashlib
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

calls = 0


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        global calls
        if self.path == "/__count":
            self.reply({"calls": calls})
        else:
            calls += 1
            body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
            self.reply({"path": self.path, "length": len(body), "headers": dict(self.headers)})

    def do_POST(self):
        global calls
        body = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        calls += 1
        self.reply({"path": self.path, "sha256": hashlib.sha256(body).hexdigest(),
                    "length": len(body), "headers": dict(self.headers)})

    def reply(self, value):
        body = json.dumps(value).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


ThreadingHTTPServer(("0.0.0.0", 8080), Handler).serve_forever()
